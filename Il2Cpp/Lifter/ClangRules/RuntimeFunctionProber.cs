using Rosetta.Binary;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.ClangRules;

/// <summary>
/// Probes ARM64 code at a given address to classify internal IL2CPP runtime functions.
///
/// ═══════════════════════════════════════════════════════════════════════════
/// SOURCE EVIDENCE CHAIN:
/// ═══════════════════════════════════════════════════════════════════════════
///
/// These functions are defined in il2cpp-codegen.h but compiled as internal
/// (non-exported) symbols. They don't appear in ELF .dynsym, so we must
/// identify them by their ARM64 instruction patterns.
///
/// 1. NullCheck(void* this_ptr) — il2cpp-codegen.h L631-637:
///      if (this_ptr != NULL) return;
///      il2cpp_codegen_raise_null_reference_exception();
///    ARM64 (Clang -Os):
///      CBNZ X0, ret_label   (or CBZ X0, throw_label)
///      B throw_helper
///      ret_label: RET
///    Pattern: 2-3 instructions, first is CBNZ/CBZ on X0
///
/// 2. il2cpp_codegen_runtime_class_init_inline(klass) — il2cpp-codegen.h L997-1001:
///      if (!klass->cctor_finished_or_no_cctor)
///          il2cpp_codegen_runtime_class_init(klass);
///    ARM64 (Clang inline):
///      LDRB W8, [X0, #offset]   // load cctor_finished flag
///      CBNZ W8, skip_label      // skip if already initialized
///      B runtime_class_init     // tail-call the full init
///      skip_label: RET
///    Pattern: 3-4 instructions, starts with LDRB from X0
///
/// 3. il2cpp_codegen_initialize_runtime_metadata_inline — il2cpp-codegen.h:
///      Loads from a global slot, checks if null, calls init if needed
///    ARM64:
///      LDR X0, [X0]             // load the metadata pointer from slot
///      CBNZ X0, skip            // already initialized?
///      ... call init ...
///    Pattern: starts with LDR from X0, then CBNZ
///
/// 4. il2cpp_codegen_object_new(klass) — il2cpp-codegen.h L817:
///      Not inline — this is a real function call.
///      Identified by frequency: called once per 'new T()' across all methods.
/// </summary>
public sealed class RuntimeFunctionProber
{
    private readonly ReadOnlyMemory<byte> _binaryData;
    private readonly IBinaryParser _elf;

    public RuntimeFunctionProber(ReadOnlyMemory<byte> binaryData, IBinaryParser elf)
    {
        _binaryData = binaryData;
        _elf = elf;
    }

    /// <summary>
    /// Probe the first few instructions at targetVA and classify the function.
    /// Returns null if the function cannot be classified.
    /// </summary>
    public string? ClassifyFunction(ulong targetVA)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    RuntimeFunctionProber.ClassifyFunction(0x{targetVA:X})");
        long fileOffset = _elf.VirtualToFileOffset(targetVA);
        if (fileOffset < 0 || fileOffset + 32 > _binaryData.Length)
            return null;

        // Decode up to 8 instructions (enough for any inline helper)
        var instructions = Arm64Decoder.DecodeBlock(
            _binaryData.Span.Slice((int)fileOffset, 32), targetVA, 8);

        if (instructions.Length < 2)
            return null;

        var i0 = instructions[0];
        if (instructions.Length >= 4)
        {
            uint raw0 = i0.RawValue;
            uint raw3 = instructions[3].RawValue;
            bool isAdrpX16 = (raw0 & 0x9F00001F) == 0x90000010;
            bool isBrX17 = raw3 == 0xD61F0220;
            if (isAdrpX16 && isBrX17)
            {
                // To get the exact GOT address, we decode the ADRP imm manually:
                uint immhi = (raw0 >> 5) & 0x7FFFF;
                uint immlo = (raw0 >> 29) & 3;
                uint imm = (immhi << 2) | immlo;
                int simm = (imm & 0x100000) != 0 ? (int)(imm - 0x200000) : (int)imm;
                
                ulong gotPage = (targetVA & ~0xFFFUL) + (ulong)((long)simm << 12);
                uint raw1 = instructions[1].RawValue; // LDR X17, [X16, #imm]
                int ldrOffset = (int)((raw1 >> 10) & 0xFFF) << 3;
                ulong gotAddr = gotPage + (ulong)ldrOffset;

                if (_elf.PltGotSymbols.TryGetValue(gotAddr, out string? symName))
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → PLT symbol: {symName}");
                    return symName; // e.g., __cxa_begin_catch
                }

                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → PLT exception handler");
                return "plt_exception_handler";
            }
        }

        var i1 = instructions[1];
        var i2 = instructions.Length > 2 ? instructions[2] : default;

        // ────────────────────────────────────────────────────────────
        // Pattern 1: NullCheck — il2cpp-codegen.h L631-637
        //   inline void NullCheck(void* this_ptr) {
        //       if (this_ptr != NULL) return;
        //       il2cpp_codegen_raise_null_reference_exception();
        //   }
        //
        // Clang -Os compiles this to:
        //   CBNZ X0, +4          (skip if not null → RET)
        //   B throw_helper       (tail call to raise_null_reference)
        //   — or —
        //   CBZ X0, throw_label  (branch if null)
        //   RET
        //   throw_label: B throw_helper
        // ────────────────────────────────────────────────────────────
        if (i0.Opcode == Arm64Opcode.CBNZ && i0.Rd == 0 &&
            i1.Opcode == Arm64Opcode.B)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → NullCheck (CBNZ)");
            return "NullCheck";
        }
        if (i0.Opcode == Arm64Opcode.CBZ && i0.Rd == 0 &&
            i1.Opcode == Arm64Opcode.RET)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → NullCheck (CBZ)");
            return "NullCheck";
        }

        // ────────────────────────────────────────────────────────────
        // Pattern 2: runtime_class_init_inline — il2cpp-codegen.h L997-1001
        //   inline void il2cpp_codegen_runtime_class_init_inline(RuntimeClass* klass) {
        //       if (!klass->cctor_finished_or_no_cctor)
        //           il2cpp_codegen_runtime_class_init(klass);
        //   }
        //
        // Clang compiles this to:
        //   LDRB W8, [X0, #offset]    // load the boolean flag
        //   CBNZ W8, ret_label        // already done → skip
        //   B il2cpp_codegen_runtime_class_init  // tail call
        //   ret_label: RET
        // ────────────────────────────────────────────────────────────
        if (i0.Opcode == Arm64Opcode.LDRB_IMM && i0.Rn == 0 &&
            i1.Opcode == Arm64Opcode.CBNZ &&
            i2.Opcode == Arm64Opcode.B)
        {
            Rosetta.Common.Constants.ClassInitFlagOffset = (int)i0.Immediate;
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → class_init_inline (LDRB + CBNZ) dynamic offset 0x{i0.Immediate:X}");
            return "il2cpp_codegen_runtime_class_init_inline";
        }
        // Also: LDRB + CBZ + RET pattern (branch over to init)
        if (i0.Opcode == Arm64Opcode.LDRB_IMM && i0.Rn == 0 &&
            i1.Opcode == Arm64Opcode.CBZ)
        {
            Rosetta.Common.Constants.ClassInitFlagOffset = (int)i0.Immediate;
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → class_init_inline (LDRB + CBZ) dynamic offset 0x{i0.Immediate:X}");
            return "il2cpp_codegen_runtime_class_init_inline";
        }

        // ────────────────────────────────────────────────────────────
        // Pattern 3: initialize_runtime_metadata_inline
        //   Checks if a global metadata slot is already populated.
        //   If yes, returns; if no, calls the full init.
        //
        // ARM64:
        //   LDR X8, [X0]         // load the slot pointer
        //   CBNZ X8, ret         // already initialized
        //   ... save/call/restore
        //   RET
        //
        // — or —
        //   LDR X0, [X0]         // load inline
        //   CBNZ X0, ret
        //   B init_func
        // ────────────────────────────────────────────────────────────
        if ((i0.Opcode == Arm64Opcode.LDR_IMM) && i0.Rn == 0 && i0.Immediate == 0 &&
            (i1.Opcode == Arm64Opcode.CBNZ || i1.Opcode == Arm64Opcode.CBZ))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → initialize_runtime_metadata_inline");
            return "il2cpp_codegen_initialize_runtime_metadata_inline";
        }

        // ────────────────────────────────────────────────────────────
        // Pattern 4: il2cpp_get_thread_static_data — Class.cpp L874
        //   void* il2cpp_get_thread_static_data(Il2CppClass* klass) {
        //       return get_thread_static_data_for_thread(
        //           klass->thread_static_fields_offset, ...);
        //   }
        //
        // ARM64 (Clang -Os):
        //   LDR W0, [X0, #0x114]    // load klass->thread_static_fields_offset
        //   B <inner_impl>          // tail-call
        //
        // The offset 0x114 (276) is the byte offset of
        // Il2CppClass::thread_static_fields_offset in the struct.
        // This is a unique, stable fingerprint for this function.
        // ────────────────────────────────────────────────────────────
        if (i0.Opcode == Arm64Opcode.LDR_IMM && i0.Rn == 0 && i0.Rd == 0 &&
            !i0.Is64Bit && i0.Immediate == 276 &&
            i1.Opcode == Arm64Opcode.B)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → get_thread_static_data");
            return "il2cpp_get_thread_static_data";
        }

        // ────────────────────────────────────────────────────────────
        // Pattern 5: Thunk/trampoline — STP + ... + BL pattern
        // Many runtime functions save callee-saved regs then call
        // the actual implementation. These are wrappers like:
        //   STP X29, X30, [SP, #-16]!
        //   MOV X29, SP
        //   BL actual_function
        //   LDP X29, X30, [SP], #16
        //   RET
        // We can't easily classify these without going deeper.
        // ────────────────────────────────────────────────────────────

        return null;
    }

    /// <summary>
    /// Probe a list of high-frequency unresolved call targets and return
    /// a mapping of address → identified function name.
    ///
    /// Source: The top-frequency unresolved calls are always IL2CPP runtime
    /// helpers (NullCheck, metadata_init, class_init, object_new).
    /// User methods are resolved via MethodAddressMap; only internal helpers
    /// remain unresolved.
    /// </summary>
    public Dictionary<ulong, string> ProbeHighFrequencyTargets(
        Dictionary<ulong, int> unresolvedCallCounts,
        int minFrequency = 5)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"RuntimeFunctionProber.ProbeHighFrequencyTargets: {unresolvedCallCounts.Count} items");
        var result = new Dictionary<ulong, string>();

        foreach (var (addr, count) in unresolvedCallCounts)
        {
            if (count < minFrequency || addr == 0) continue;

            string? classification = ClassifyFunction(addr);
            if (classification != null)
            {
                result[addr] = classification;
            }
            else if (count >= 50)
            {
                // Very high frequency unresolved calls that we can't classify
                // by instruction pattern are likely non-inline runtime helpers.
                // Source: il2cpp_codegen_object_new (L817) is not inline.
                // We mark them as "unknown_runtime_helper" rather than guessing.
                result[addr] = "il2cpp_runtime_helper";
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  RuntimeFunctionProber found {result.Count} runtime functions");
        return result;
    }
}
