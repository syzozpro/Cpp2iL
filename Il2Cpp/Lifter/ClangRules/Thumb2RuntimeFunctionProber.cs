using System;
using Rosetta.Binary;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.ClangRules;

/// <summary>
/// Probes ARM32 (Thumb2) code at a given address to classify internal IL2CPP runtime functions.
///
/// ═══════════════════════════════════════════════════════════════════════════
/// THUMB2 PATTERN EVIDENCE:
/// ═══════════════════════════════════════════════════════════════════════════
///
/// These patterns were discovered and validated against a V104 ARM32 IL2CPP
/// binary (armeabi-v7a). Patterns match the same IL2CPP source as ARM64 but
/// compiled with Clang -mthumb targeting ARMv7.
///
/// 1. NullCheck — il2cpp-codegen.h L631-637:
///    Thumb2 patterns:
///      CBZ/CBNZ R0, label     ; check if null
///      B throw_helper         ; tail call to raise_null_reference
///    Or:
///      PUSH; CBZ/CBNZ R0, ...
///
/// 2. runtime_class_init_inline — il2cpp-codegen.h L997-1001:
///    Thumb2 patterns:
///      LDRB Rx, [R0, #off]    ; load cctor_finished flag
///      CBZ/CBNZ Rx, skip      ; skip if already initialized
///      B/BL runtime_class_init
///    Wrapped form:
///      PUSH; ADD R7,SP; MOV Rx,R0; LDRB Ry,[R0,#off]; CBZ/CBNZ
///
/// 3. initialize_runtime_metadata_inline — il2cpp-codegen.h:
///    Thumb2 patterns:
///      LDR Rx, [R0]           ; load metadata pointer from slot
///      CBZ/CBNZ Rx, skip      ; already initialized?
///    Wrapped form:
///      PUSH; ADD R7,SP; MOV Rx,R0; LDR R0,[R0]; CBZ/CBNZ R0
/// </summary>
public sealed class Thumb2RuntimeFunctionProber
{
    private readonly ReadOnlyMemory<byte> _binaryData;
    private readonly IBinaryParser _elf;

    public Thumb2RuntimeFunctionProber(ReadOnlyMemory<byte> binaryData, IBinaryParser elf)
    {
        _binaryData = binaryData;
        _elf = elf;
    }

    /// <summary>
    /// Probe the first few Thumb2 instructions at targetVA and classify the function.
    /// Returns null if the function cannot be classified.
    /// </summary>
    public string? ClassifyFunction(ulong targetVA)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    Thumb2RuntimeFunctionProber.ClassifyFunction(0x{targetVA:X})");

        // Check if target is in PLT range (ARM-mode, not Thumb2 code)
        if (_elf.PltStubSymbols.TryGetValue(targetVA, out string? pltSym))
            return pltSym;

        long fileOffset = _elf.VirtualToFileOffset(targetVA);
        if (fileOffset < 0 || fileOffset + 24 > _binaryData.Length)
            return null;

        // Read enough bytes to decode up to ~6 instructions
        var span = _binaryData.Span.Slice((int)fileOffset, 24);

        // Decode first 6 instructions as (mnemonic-like, operand-like) using raw Thumb2 decoding
        // We parse the raw opcodes directly — no Capstone dependency in C#
        var instrs = new Thumb2InstrInfo[8];
        int nInstrs = 0;
        int pos = 0;
        while (pos < span.Length - 1 && nInstrs < 8)
        {
            instrs[nInstrs] = DecodeThumb2Instr(span, pos, (uint)(targetVA + (uint)pos));
            pos += instrs[nInstrs].Size;
            nInstrs++;
        }

        if (nInstrs < 2) return null;

        ref var i0 = ref instrs[0];
        ref var i1 = ref instrs[1];
        ref var i2 = ref instrs[2];
        ref var i3 = ref instrs[3];
        ref var i4 = ref instrs[4];

        // ── Pattern 1: NullCheck ──
        // CBZ R0, label → B/BL ... or CBNZ R0, label → B
        if ((i0.Kind == InstrKind.CBZ_R0 || i0.Kind == InstrKind.CBNZ_R0) &&
            (i1.Kind == InstrKind.B || i1.Kind == InstrKind.BX_LR || i1.Kind == InstrKind.POP))
            return "NullCheck";

        // CMP R0, #0 → Bcond
        if (i0.Kind == InstrKind.CMP_R0_0 && i1.Kind == InstrKind.Bcond)
            return "NullCheck";

        // ── Pattern 2: runtime_class_init_inline ──
        // LDRB Rx, [R0, #off] → CBZ/CBNZ
        if (i0.Kind == InstrKind.LDRB_R0 &&
            (i1.Kind == InstrKind.CBZ || i1.Kind == InstrKind.CBNZ ||
             i1.Kind == InstrKind.CBZ_R0 || i1.Kind == InstrKind.CBNZ_R0))
            return "il2cpp_codegen_runtime_class_init_inline";

        if (i0.Kind == InstrKind.LDRB_R0 && i1.Kind == InstrKind.CMP && i2.Kind == InstrKind.Bcond)
            return "il2cpp_codegen_runtime_class_init_inline";

        // ── Pattern 3: initialize_runtime_metadata_inline ──
        // LDR Rx, [R0] → CBZ/CBNZ
        if (i0.Kind == InstrKind.LDR_R0_deref &&
            (i1.Kind == InstrKind.CBZ || i1.Kind == InstrKind.CBNZ ||
             i1.Kind == InstrKind.CBZ_R0 || i1.Kind == InstrKind.CBNZ_R0))
            return "il2cpp_codegen_initialize_runtime_metadata_inline";

        if (i0.Kind == InstrKind.LDR_R0_deref && i1.Kind == InstrKind.CMP_R0_0 && i2.Kind == InstrKind.Bcond)
            return "il2cpp_codegen_initialize_runtime_metadata_inline";

        // ── Pattern 4: Wrapped forms with PUSH prologue ──
        if (i0.Kind == InstrKind.PUSH && nInstrs >= 5)
        {
            // PUSH + CBZ/CBNZ R0
            if (i1.Kind == InstrKind.CBZ_R0 || i1.Kind == InstrKind.CBNZ_R0)
                return "NullCheck";

            // PUSH + LDRB [R0,#off] + CBZ/CBNZ
            if (i1.Kind == InstrKind.LDRB_R0 &&
                (i2.Kind == InstrKind.CBZ || i2.Kind == InstrKind.CBNZ ||
                 i2.Kind == InstrKind.CBZ_R0 || i2.Kind == InstrKind.CBNZ_R0 ||
                 i2.Kind == InstrKind.CMP))
                return "il2cpp_codegen_runtime_class_init_inline";

            // PUSH + LDR [R0] + CBZ/CBNZ
            if (i1.Kind == InstrKind.LDR_R0_deref &&
                (i2.Kind == InstrKind.CBZ || i2.Kind == InstrKind.CBNZ ||
                 i2.Kind == InstrKind.CBZ_R0 || i2.Kind == InstrKind.CBNZ_R0 ||
                 i2.Kind == InstrKind.CMP))
                return "il2cpp_codegen_initialize_runtime_metadata_inline";

            // PUSH; ADD R7,SP; MOV Rx,R0; LDR R0,[R0]; CBZ/CBNZ R0
            if (i1.Kind == InstrKind.ADD_R7_SP && i2.Kind == InstrKind.MOV_from_R0 &&
                i3.Kind == InstrKind.LDR_R0_deref &&
                (i4.Kind == InstrKind.CBZ_R0 || i4.Kind == InstrKind.CBNZ_R0))
                return "il2cpp_codegen_initialize_runtime_metadata_inline";

            // PUSH; ADD R7,SP; MOV Rx,R0; LDRB Ry,[R0,#off]; CBZ/CBNZ
            if (i1.Kind == InstrKind.ADD_R7_SP && i2.Kind == InstrKind.MOV_from_R0 &&
                i3.Kind == InstrKind.LDRB_R0 &&
                (i4.Kind == InstrKind.CBZ || i4.Kind == InstrKind.CBNZ ||
                 i4.Kind == InstrKind.CBZ_R0 || i4.Kind == InstrKind.CBNZ_R0 ||
                 i4.Kind == InstrKind.CMP))
                return "il2cpp_codegen_runtime_class_init_inline";
        }

        return null;
    }

    /// <summary>
    /// Probe a list of high-frequency unresolved call targets and return
    /// a mapping of address → identified function name.
    /// </summary>
    public Dictionary<ulong, string> ProbeHighFrequencyTargets(
        Dictionary<ulong, int> unresolvedCallCounts,
        int minFrequency = 5)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"Thumb2RuntimeFunctionProber.ProbeHighFrequencyTargets: {unresolvedCallCounts.Count} items");
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
                // Very high frequency unresolved calls — likely non-inline runtime helpers
                result[addr] = "il2cpp_runtime_helper";
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  Thumb2RuntimeFunctionProber found {result.Count} runtime functions");
        return result;
    }

    // ════════════════════════════════════════════════════════════════
    // Minimal Thumb2 instruction decoder (pattern matching only)
    // ════════════════════════════════════════════════════════════════

    private enum InstrKind
    {
        Unknown,
        PUSH,
        POP,
        B,
        Bcond,
        BL,
        BLX,
        BX_LR,
        CBZ,
        CBZ_R0,
        CBNZ,
        CBNZ_R0,
        CMP,
        CMP_R0_0,
        LDR_R0_deref,   // LDR Rx, [R0] or LDR R0, [R0]
        LDRB_R0,         // LDRB Rx, [R0, #imm]
        ADD_R7_SP,        // ADD R7, SP, #imm
        MOV_from_R0,      // MOV Rx, R0 (where Rx != R0)
    }

    private readonly struct Thumb2InstrInfo
    {
        public readonly InstrKind Kind;
        public readonly int Size;

        public Thumb2InstrInfo(InstrKind kind, int size)
        {
            Kind = kind;
            Size = size;
        }
    }

    /// <summary>
    /// Decode a Thumb2 instruction into a simplified InstrKind for pattern matching.
    /// This is NOT a full disassembler — it only classifies instructions relevant
    /// to runtime helper identification.
    /// </summary>
    private static Thumb2InstrInfo DecodeThumb2Instr(ReadOnlySpan<byte> data, int offset, uint va)
    {
        if (offset + 2 > data.Length) return new(InstrKind.Unknown, 2);

        ushort hw0 = (ushort)(data[offset] | (data[offset + 1] << 8));

        // 16-bit instructions
        if ((hw0 >> 11) < 0b11101)
        {
            // PUSH: 1011_0100_xxxx_xxxx (B4xx) or 1011_0101_xxxx_xxxx (B5xx with LR)
            if ((hw0 & 0xFE00) == 0xB400)
                return new(InstrKind.PUSH, 2);

            // POP: 1011_1100_xxxx_xxxx (BCxx) or 1011_1101_xxxx_xxxx (BDxx with PC)
            if ((hw0 & 0xFE00) == 0xBC00)
                return new(InstrKind.POP, 2);

            // CBZ/CBNZ: 16-bit Thumb
            // CBZ:  1011_0_0_i_1_iiiii_nnn → (hw0 & 0xF500) == 0xB100
            // CBNZ: 1011_1_0_i_1_iiiii_nnn → (hw0 & 0xF500) == 0xB900
            if ((hw0 & 0xF500) == 0xB100)
            {
                int rn = hw0 & 0x7;
                return rn == 0
                    ? new(InstrKind.CBZ_R0, 2)
                    : new(InstrKind.CBZ, 2);
            }
            if ((hw0 & 0xF500) == 0xB900)
            {
                int rn = hw0 & 0x7;
                return rn == 0
                    ? new(InstrKind.CBNZ_R0, 2)
                    : new(InstrKind.CBNZ, 2);
            }

            // CMP Rn, #imm8: 0010_1nnn_iiii_iiii
            if ((hw0 >> 11) == 0b00101)
            {
                int rn = (hw0 >> 8) & 0x7;
                int imm = hw0 & 0xFF;
                if (rn == 0 && imm == 0) return new(InstrKind.CMP_R0_0, 2);
                return new(InstrKind.CMP, 2);
            }

            // ADD Rd, SP, #imm8: 1010_1ddd_iiii_iiii
            if ((hw0 >> 11) == 0b10101)
            {
                int rd = (hw0 >> 8) & 0x7;
                if (rd == 7) return new(InstrKind.ADD_R7_SP, 2);
                return new(InstrKind.Unknown, 2);
            }

            // MOV Rd, Rm (high registers): 0100_0110_D_Rm_Rd
            if ((hw0 & 0xFF00) == 0x4600)
            {
                int rm = (hw0 >> 3) & 0xF;
                int rd = ((hw0 >> 4) & 0x8) | (hw0 & 0x7);
                if (rm == 0 && rd != 0) return new(InstrKind.MOV_from_R0, 2);
                return new(InstrKind.Unknown, 2);
            }

            // LDR Rt, [Rn, #imm5]: 0110_1iii_iinn_nttt
            if ((hw0 >> 11) == 0b01101)
            {
                int rn = (hw0 >> 3) & 0x7;
                int imm5 = (hw0 >> 6) & 0x1F;
                if (rn == 0 && imm5 == 0) return new(InstrKind.LDR_R0_deref, 2);
                return new(InstrKind.Unknown, 2);
            }

            // LDRB Rt, [Rn, #imm5]: 0111_1iii_iinn_nttt
            if ((hw0 >> 11) == 0b01111)
            {
                int rn = (hw0 >> 3) & 0x7;
                if (rn == 0) return new(InstrKind.LDRB_R0, 2);
                return new(InstrKind.Unknown, 2);
            }

            // BX LR: 0100_0111_0111_0000 = 0x4770
            if (hw0 == 0x4770) return new(InstrKind.BX_LR, 2);

            // Conditional branch: 1101_cccc_iiii_iiii (B<cond>)
            if ((hw0 >> 12) == 0b1101 && ((hw0 >> 8) & 0xF) < 0xE)
                return new(InstrKind.Bcond, 2);

            // Unconditional branch: 1110_0iii_iiii_iiii (B)
            if ((hw0 >> 11) == 0b11100)
                return new(InstrKind.B, 2);

            return new(InstrKind.Unknown, 2);
        }

        // 32-bit instructions
        if (offset + 4 > data.Length) return new(InstrKind.Unknown, 2);
        ushort hw1 = (ushort)(data[offset + 2] | (data[offset + 3] << 8));

        // BL: hw0[15:11]=11110, hw1[15:14]=11, hw1[12]=1
        if ((hw0 >> 11) == 0b11110 && (hw1 >> 14) == 0b11 && ((hw1 >> 12) & 1) == 1)
            return new(InstrKind.BL, 4);

        // BLX: hw0[15:11]=11110, hw1[15:14]=11, hw1[12]=0
        if ((hw0 >> 11) == 0b11110 && (hw1 >> 14) == 0b11 && ((hw1 >> 12) & 1) == 0)
            return new(InstrKind.BLX, 4);

        // PUSH.W: 1110_1001_0010_1101 1_xxx_xxxx_xxxx_xxxx = E92D xxxx
        if ((hw0 & 0xFFFF) == 0xE92D)
            return new(InstrKind.PUSH, 4);

        // POP.W: 1110_1000_1011_1101 = E8BD
        if ((hw0 & 0xFFFF) == 0xE8BD)
            return new(InstrKind.POP, 4);

        // LDR.W Rt, [Rn, #imm12]: 1111_1000_1101_nnnn tttt_iiii_iiii_iiii
        if ((hw0 & 0xFFF0) == 0xF8D0)
        {
            int rn = hw0 & 0xF;
            int imm12 = hw1 & 0xFFF;
            if (rn == 0 && imm12 == 0) return new(InstrKind.LDR_R0_deref, 4);
            return new(InstrKind.Unknown, 4);
        }

        // LDRB.W Rt, [Rn, #imm12]: 1111_1000_1001_nnnn tttt_iiii_iiii_iiii
        if ((hw0 & 0xFFF0) == 0xF890)
        {
            int rn = hw0 & 0xF;
            if (rn == 0) return new(InstrKind.LDRB_R0, 4);
            return new(InstrKind.Unknown, 4);
        }

        // CMP.W Rn, #const: 1111_0i01_0001_nnnn 0iii_1111_iiii_iiii
        if ((hw0 & 0xFBF0) == 0xF1B0 && (hw1 & 0x8F00) == 0x0F00)
        {
            int rn = hw0 & 0xF;
            int imm = hw1 & 0xFF; // simplified — doesn't decode modified constant
            if (rn == 0 && imm == 0) return new(InstrKind.CMP_R0_0, 4);
            return new(InstrKind.CMP, 4);
        }

        // ADD.W with SP: many encodings, just check common ones
        // ADD.W Rd, SP, #const: 1111_0i01_0000_1101 0iii_dddd_iiii_iiii
        if ((hw0 & 0xFBFF) == 0xF10D)
        {
            int rd = (hw1 >> 8) & 0xF;
            if (rd == 7) return new(InstrKind.ADD_R7_SP, 4);
            return new(InstrKind.Unknown, 4);
        }

        // B.W conditional: 1111_0Scc_ccii_iiii 10j0_jiii_iiii_iiii
        if ((hw0 >> 11) == 0b11110 && (hw1 & 0xD000) == 0x8000)
            return new(InstrKind.Bcond, 4);

        // B.W unconditional: 1111_0Sii_iiii_iiii 10j1_jiii_iiii_iiii
        if ((hw0 >> 11) == 0b11110 && (hw1 & 0xD000) == 0x9000)
            return new(InstrKind.B, 4);

        return new(InstrKind.Unknown, 4);
    }
}
