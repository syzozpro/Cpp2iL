using Rosetta.Binary;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.Resolve;

/// <summary>
/// Classifies unresolved IL2CPP runtime helper calls by analyzing the call-site
/// context — the instructions immediately BEFORE and AFTER the BL.
///
/// ═══════════════════════════════════════════════════════════════════════════
/// EVIDENCE CHAIN (Source: il2cpp-codegen.h from native_research)
/// ═══════════════════════════════════════════════════════════════════════════
///
/// Many IL2CPP internal functions are compiled with full prologues (STP/STR)
/// and can't be classified by their own instruction pattern alone.
/// Instead, we use the CALLING convention — what the codegen always emits
/// around each specific call — to identify them.
///
/// The IL2CPP transpiler (CodeWriterExtensions.cs, MethodBodyWriter.cs) emits
/// highly stereotyped instruction sequences for each C++ helper. These
/// sequences are invariant across all call sites.
/// </summary>
public sealed class CallSiteClassifier
{
    /// <summary>
    /// Classify a BL target by examining surrounding instructions.
    ///
    /// Returns null if the context doesn't match any known pattern.
    /// </summary>
    public static string? ClassifyFromContext( Arm64Instruction[] instructions,  int blIndex)
    {
        if (blIndex < 0 || blIndex >= instructions.Length) return null;
        var bl = instructions[blIndex];
        if (bl.Opcode != Arm64Opcode.BL) return null;
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    CallSiteClassifier: BL at idx={blIndex} target=0x{bl.Immediate:X}");

        // === Pattern 1: il2cpp_codegen_runtime_class_init ===
        // Source: il2cpp-codegen.h:997-1000
        //   il2cpp_codegen_runtime_class_init_inline:
        //     if (!klass->cctor_finished_or_no_cctor) il2cpp_codegen_runtime_class_init(klass);
        //
        // ARM64 codegen (CodeWriterExtensions.cs):
        //   Variant A: LDR W9, [X8, #0xE4] → CBNZ W9 → MOV X0, X8 → BL
        //   Variant B: LDR W8, [X0, #0xE4] → CBNZ W8 → BL (X0 already has klass)
        if (blIndex >= 3)
        {
            var m3 = instructions[blIndex - 3];
            var m2 = instructions[blIndex - 2];
            var m1 = instructions[blIndex - 1];

            if (m3.Opcode == Arm64Opcode.LDR_IMM && m3.Immediate == 0xE4 &&
                m2.Opcode == Arm64Opcode.CBNZ &&
                m1.Opcode == Arm64Opcode.MOV_REG && m1.Rd == 0)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → class_init (3-instr pattern)");
                return "il2cpp_codegen_runtime_class_init";
            }
        }
        // Variant B: X0 already loaded with klass, 2-instruction pattern
        //   LDR W8, [X0, #0xE4]
        //   CBNZ W8, skip
        //   BL runtime_class_init     // ← X0 still has klass
        // === Pattern 1: il2cpp_codegen_runtime_class_init ===
        // Source: il2cpp-codegen.h:995
        //   inline void il2cpp_codegen_runtime_class_init_inline(RuntimeClass* klass)
        //   {
        //       if (!klass->has_cctor || klass->cctor_finished_or_no_cctor) return;
        //       il2cpp_codegen_runtime_class_init(klass);
        //   }
        //
        // Structural ABI truth:
        //   X0 = RuntimeClass*
        //   There MUST be a read of a flag (cctor_finished) into Wn and a conditional branch (CBNZ/CBZ Wn)
        //   somewhere before the call to skip it if already initialized.
        int scanStartInit = Math.Max(0, blIndex - 6);
        int cbnzReg = -1;
        
        for (int j = blIndex - 1; j >= scanStartInit; j--)
        {
            var m = instructions[j];
            if (m.Opcode == Arm64Opcode.CBNZ || m.Opcode == Arm64Opcode.CBZ)
            {
                cbnzReg = m.Rd;
                break;
            }
        }
        
        if (cbnzReg != -1)
        {
            for (int j = blIndex - 1; j >= scanStartInit; j--)
            {
                var m = instructions[j];
                // LDRB Wn, [X0, #0xE4] or LDR Wn, [X0, #0xE4]
                // Must check offset 0xE4 (cctor_finished_or_no_cctor) to avoid false positives
                if ((m.Opcode == Arm64Opcode.LDRB_IMM || m.Opcode == Arm64Opcode.LDR_IMM) && m.Rd == cbnzReg && m.Rn == 0
                    && m.Immediate == 0xE4)
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → class_init (CBNZ variant)");
                    return "il2cpp_codegen_runtime_class_init";
                }
            }
        }

        // === Pattern 2: il2cpp_codegen_initialize_runtime_metadata ===
        // Source: il2cpp-codegen.h:1067, CodeWriterExtensions.cs:273
        //   Called inside s_Il2CppMethodInitialized == false block.
        //   X0 = metadata pointer (from ADRP+LDR to .data/.got)
        //   Followed by: init flag = true, then STRB to .bss
        //
        // ARM64 codegen:
        //   LDR X0, [Xn, #off]   ; metadata token (RuntimeClass, string, MethodInfo)
        //   BL init_metadata      ; ← this call
        //   ... (may have more init calls or MOVZ #1 + STRB)
        // === Pattern 2: il2cpp_codegen_initialize_runtime_metadata ===
        // Source: CodeWriterExtensions.cs:273
        //   il2cpp_codegen_initialize_runtime_metadata((uintptr_t*)&s_Il2CppMetadataRegistration...)
        //
        // Structural ABI truth:
        //   X0 must be a pointer to the metadata usages table (usually in .bss or .data).
        //   This is typically loaded via ADRP + ADD or ADRP + LDR.
        int scanStartMeta = Math.Max(0, blIndex - 6);
        bool hasAdrpForMetadata = false;
        bool hasMetadataPtrInX0 = false;
        
        for (int j = blIndex - 1; j >= scanStartMeta; j--)
        {
            var m = instructions[j];
            if (m.Opcode == Arm64Opcode.ADRP)
            {
                hasAdrpForMetadata = true;
            }
            if (m.Opcode == Arm64Opcode.LDR_IMM && m.Rd == 0)
            {
                hasMetadataPtrInX0 = true;
            }
        }
        
        if (hasAdrpForMetadata && hasMetadataPtrInX0)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → init_metadata (ADRP+LDR pattern)");
            return "il2cpp_codegen_initialize_runtime_metadata";
        }
        
        // Also: sometimes several BL init calls in sequence without repeated ADRP.
        // Each one is preceded by LDR X0, [Xn, #off] (metadata ptr)
        if (hasMetadataPtrInX0)
        {
            if (blIndex + 1 < instructions.Length)
            {
                var p1 = instructions[blIndex + 1];
                // Next instruction is ADRP or LDR into X0 → another init call
                if (p1.Opcode == Arm64Opcode.ADRP || 
                    (p1.Opcode == Arm64Opcode.LDR_IMM && p1.Rd == 0))
                {
                    return "il2cpp_codegen_initialize_runtime_metadata";
                }
            }
            // Or preceded by a previous metadata init
            if (blIndex - 2 >= 0 && instructions[blIndex - 2].Opcode == Arm64Opcode.BL)
            {
                return "il2cpp_codegen_initialize_runtime_metadata";
            }
        }

        // === Pattern 3: Box(RuntimeClass* type, void* data) ===
        // Source: il2cpp-codegen.h:641
        //   RuntimeObject* Box(RuntimeClass* type, void* data);
        //
        // Structural ABI truth:
        //   X0 = RuntimeClass* (metadata)
        //   X1 = Address of value type (ADD X1, SP, #off OR ADD X1, Xn, #off)
        // Passing a memory address in X1 via ADD right before a call is highly unique to Box.
        int scanStartBox = Math.Max(0, blIndex - 6);
        bool hasAddX1 = false;
        bool hasTypeLoadX0 = false;
        
        for (int j = blIndex - 1; j >= scanStartBox; j--)
        {
            var m = instructions[j];
            if (m.Opcode == Arm64Opcode.ADD_IMM && m.Rd == 1)
                hasAddX1 = true;
            if (m.Opcode == Arm64Opcode.LDR_IMM && m.Rd == 0)
                hasTypeLoadX0 = true;
                
            if (hasAddX1 && hasTypeLoadX0)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → Box (ADD X1 + LDR X0)");
                return "Box";
            }
        }

        // === Pattern 4: SZArrayNew(RuntimeClass* arrayType, uint32_t length) ===
        // Source: il2cpp-codegen.h:803
        //   RuntimeArray* SZArrayNew(RuntimeClass* arrayType, uint32_t length);
        //
        // Structural ABI truth:
        //   X0 = RuntimeClass*
        //   W1 = length (from MOVZ or moved from another reg)
        int scanStartArray = Math.Max(0, blIndex - 4);
        bool hasLengthW1 = false;
        
        for (int j = blIndex - 1; j >= scanStartArray; j--)
        {
            var m = instructions[j];
            if ((m.Opcode == Arm64Opcode.MOVZ && m.Rd == 1) || 
                (m.Opcode == Arm64Opcode.MOV_REG && m.Rd == 1))
            {
                hasLengthW1 = true;
                break;
            }
        }
        
        if (hasLengthW1)
        {
            for (int j = blIndex - 1; j >= scanStartArray; j--)
            {
                var m = instructions[j];
                if (m.Opcode == Arm64Opcode.LDR_IMM && m.Rd == 0)
                    return "SZArrayNew";
            }
        }

        // === Pattern 5: Il2CppCodeGenWriteBarrier ===
        // Source: il2cpp-codegen.h
        //   void Il2CppCodeGenWriteBarrier(void** targetAddress, void* object);
        //
        // Structural ABI truth:
        //   X0 = target memory address (usually inside an object)
        //   X1 = reference object being stored
        //   A store (STR) of X1 to [X0] must happen before the call.
        // Due to Clang optimizations, the exact registers might be moved around 
        // (e.g. STR Xn, [Xm] then MOV X0, Xm; MOV X1, Xn).
        int scanStartBarrier = Math.Max(0, blIndex - 6);
        bool hasWriteBarrierStore = false;
        
        for (int j = blIndex - 1; j >= scanStartBarrier; j--)
        {
            var m = instructions[j];
            // Look for any STR instruction that writes a register into memory
            // WriteBarrier is only used for object references, so usually STR Xn
            if (m.Opcode == Arm64Opcode.STR_IMM)
            {
                // If it's a pre-indexed store (writeback), it's highly likely a write barrier
                if (m.Writeback > 0)
                {
                    hasWriteBarrierStore = true;
                    break;
                }
                
                // If it's a regular store, we need to verify X0 and X1 are set up
                for (int k = blIndex - 1; k > j; k--)
                {
                    if (instructions[k].Opcode == Arm64Opcode.MOV_REG && 
                       (instructions[k].Rd == 0 || instructions[k].Rd == 1))
                    {
                        hasWriteBarrierStore = true;
                        break;
                    }
                }
                if (hasWriteBarrierStore) break;
            }
        }
        
        if (hasWriteBarrierStore)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → WriteBarrier");
            return "Il2CppCodeGenWriteBarrier";
        }

        // === Pattern 6: IsInst/CastClass ===
        // Source: il2cpp-codegen.h (IsInst, CastclassClass)
        //   X0 = object, X1 = RuntimeClass* (from vtable offset [X8, #0x40])
        //   Preceded by null check (CBZ X0)
        //
        // Structural ABI truth:
        //   LDR X1, [Xn, #0x40] occurs before the call.
        int scanStartIsInst = Math.Max(0, blIndex - 6);
        for (int j = blIndex - 1; j >= scanStartIsInst; j--)
        {
            var m = instructions[j];
            if (m.Opcode == Arm64Opcode.LDR_IMM && m.Rd == 1 && m.Immediate == 0x40)
            {
                return "IsInst";
            }
        }

        // === Pattern 7: il2cpp_codegen_object_new / il2cpp_type_get_object ===
        // Source: il2cpp-codegen.h:817
        //   RuntimeObject* il2cpp_codegen_object_new(RuntimeClass *klass);
        //   X0 = RuntimeClass* / Il2CppType* (dereferenced from metadata, LDR X0, [Xn])
        //   No second argument — distinguishes from Box (X1=&stack) and SZArrayNew (W1=count)
        int scanStartNew = Math.Max(0, blIndex - 6);
        bool hasLdrX0 = false;
        bool hasX1Mod = false;
        
        for (int j = blIndex - 1; j >= scanStartNew; j--)
        {
            var m = instructions[j];
            if (m.Opcode == Arm64Opcode.LDR_IMM && m.Rd == 0)
                hasLdrX0 = true;
            
            // If X1 is modified in any way, it might be Box, SZArrayNew, WriteBarrier, IsInst
            if (m.Rd == 1 && (m.Opcode == Arm64Opcode.MOV_REG || m.Opcode == Arm64Opcode.MOVZ || m.Opcode == Arm64Opcode.ADD_IMM || m.Opcode == Arm64Opcode.LDR_IMM))
                hasX1Mod = true;
        }
        
        if (hasLdrX0 && !hasX1Mod)
        {
            // Distinguish il2cpp_codegen_object_new from il2cpp_type_get_object
            // Object new is followed by constructor call (BL) where the allocated object is passed in X0.
            int nextBl = -1;
            for (int j = blIndex + 1; j < Math.Min(instructions.Length, blIndex + 15); j++)
            {
                if (instructions[j].Opcode == Arm64Opcode.BL)
                {
                    nextBl = j;
                    break;
                }
            }

            if (nextBl != -1)
            {
                // Trace if X0 at nextBl holds the allocated object
                int currentReg = 0; // starts in X0
                bool killed = false;
                for (int j = blIndex + 1; j < nextBl; j++)
                {
                    var inst = instructions[j];
                    if (inst.Opcode == Arm64Opcode.MOV_REG)
                    {
                        if (inst.Rn == currentReg)
                        {
                            currentReg = inst.Rd; // tracked register moved to Rd
                        }
                        else if (inst.Rd == currentReg)
                        {
                            killed = true; // tracked register overwritten
                            break;
                        }
                    }
                    else if (inst.Rd == currentReg)
                    {
                        // Any other instruction writing to currentReg kills it
                        killed = true;
                        break;
                    }
                }

                if (!killed && currentReg == 0)
                {
                    return "il2cpp_codegen_object_new";
                }
            }

            return "il2cpp_type_get_object";
        }

        // === Pattern 8: il2cpp_codegen_get_active_exception ===
        // Source: MethodBodyWriter.cs (IL2CPP_GET_ACTIVE_EXCEPTION)
        //   RuntimeObject* il2cpp_codegen_get_active_exception();
        //
        // Structural ABI truth:
        //   Takes NO arguments. Returns exception pointer in X0.
        //   Often called immediately after __cxa_begin_catch (which is a BL to a PLT stub).
        //   Followed immediately by storing the result (MOV Xn, X0).
        if (blIndex >= 1 && blIndex + 1 < instructions.Length)
        {
            var mPrev = instructions[blIndex - 1];
            var mNext = instructions[blIndex + 1];
            
            // If preceded by another BL (like __cxa_begin_catch) and followed by MOV Xn, X0
            if (mPrev.Opcode == Arm64Opcode.BL && 
                mNext.Opcode == Arm64Opcode.MOV_REG && mNext.Rn == 0 && mNext.Rd != 0)
            {
                return "il2cpp_codegen_get_active_exception";
            }
        }

        // === Pattern 9: il2cpp_resolve_icall / il2cpp_codegen_string_new_wrapper ===
        // Both take a const char* in X0 (loaded via ADRP + ADD or ADR).
        // il2cpp_resolve_icall resolves an icall name and caches the resolved pointer.
        // It is typically guarded by a null-check of the cache variable and followed by a store to it.
        int addIdx = -1;
        int scanStartStr = Math.Max(0, blIndex - 6);
        for (int j = blIndex - 1; j >= scanStartStr; j--)
        {
            var m = instructions[j];
            if (m.Opcode == Arm64Opcode.ADR && m.Rd == 0)
            {
                addIdx = j;
                break;
            }
            if (m.Opcode == Arm64Opcode.ADD_IMM && m.Rd == 0)
            {
                // Verify the source of ADD is from an ADRP
                int addRn = m.Rn;
                for (int k = j - 1; k >= scanStartStr; k--)
                {
                    if (instructions[k].Opcode == Arm64Opcode.ADRP && instructions[k].Rd == addRn)
                    {
                        addIdx = j;
                        break;
                    }
                }
                if (addIdx != -1) break;
            }
        }

        if (addIdx != -1)
        {
            // Check if it is il2cpp_resolve_icall by looking for a guard branch before and store after
            bool hasGuard = false;
            int scanStartGuard = Math.Max(0, blIndex - 10);
            for (int j = blIndex - 1; j >= scanStartGuard; j--)
            {
                var m = instructions[j];
                if (m.Opcode == Arm64Opcode.CBNZ || m.Opcode == Arm64Opcode.CBZ)
                {
                    hasGuard = true;
                    break;
                }
            }

            bool hasStoreAfter = false;
            int scanEndStore = Math.Min(instructions.Length, blIndex + 5);
            for (int j = blIndex + 1; j < scanEndStore; j++)
            {
                var m = instructions[j];
                if (m.Opcode == Arm64Opcode.STR_IMM && m.Rd == 0) // STR X0, [Xn, #off]
                {
                    hasStoreAfter = true;
                    break;
                }
            }

            if (hasGuard && hasStoreAfter)
            {
                return "il2cpp_resolve_icall";
            }
            
            return "il2cpp_codegen_string_new_wrapper";
        }

        // === Pattern 10: il2cpp_object_unbox ===
        // Signature: void* il2cpp_object_unbox(RuntimeObject* obj)
        // X0 = boxed object pointer (MOV X0, Xn or LDR X0, [Xn])
        // Returns the address of the unboxed value in X0, which is immediately used/dereferenced.
        int scanStartUnbox = Math.Max(0, blIndex - 4);
        bool hasObjInX0 = false;
        for (int j = blIndex - 1; j >= scanStartUnbox; j--)
        {
            var m = instructions[j];
            if ((m.Opcode == Arm64Opcode.MOV_REG && m.Rd == 0) ||
                (m.Opcode == Arm64Opcode.LDR_IMM && m.Rd == 0))
            {
                hasObjInX0 = true;
                break;
            }
        }

        if (hasObjInX0)
        {
            // Verify that X0 is used immediately after the call
            bool x0UsedAfter = false;
            int scanEndUnbox = Math.Min(instructions.Length, blIndex + 4);
            for (int j = blIndex + 1; j < scanEndUnbox; j++)
            {
                var m = instructions[j];
                // Check if X0 is a source register (Rn, Rm, or Rd for store)
                if (m.Rn == 0 || m.Rm == 0 || (m.Opcode == Arm64Opcode.STR_IMM && m.Rd == 0))
                {
                    x0UsedAfter = true;
                    break;
                }
                if (m.Opcode == Arm64Opcode.MOV_REG && m.Rn == 0) // MOV Xn, X0
                {
                    x0UsedAfter = true;
                    break;
                }
            }

            if (x0UsedAfter)
            {
                return "il2cpp_object_unbox";
            }
        }

        // === Pattern 11: il2cpp_codegen_raise_exception ===
        // Signature: void il2cpp_codegen_raise_exception(Exception_t* ex)
        // X0 = exception object pointer. Never returns.
        int scanStartRaise = Math.Max(0, blIndex - 4);
        bool hasExceptionInX0 = false;
        for (int j = blIndex - 1; j >= scanStartRaise; j--)
        {
            var m = instructions[j];
            if ((m.Opcode == Arm64Opcode.MOV_REG && m.Rd == 0) ||
                (m.Opcode == Arm64Opcode.LDR_IMM && m.Rd == 0))
            {
                hasExceptionInX0 = true;
                break;
            }
        }

        if (hasExceptionInX0)
        {
            // Verify that X0 is NOT used after (no-return / void return)
            bool x0UsedAfter = false;
            int scanEndRaise = Math.Min(instructions.Length, blIndex + 4);
            for (int j = blIndex + 1; j < scanEndRaise; j++)
            {
                var m = instructions[j];
                if (m.Rn == 0 || m.Rm == 0 || (m.Opcode == Arm64Opcode.STR_IMM && m.Rd == 0))
                {
                    x0UsedAfter = true;
                    break;
                }
                if (m.Opcode == Arm64Opcode.MOV_REG && m.Rn == 0)
                {
                    x0UsedAfter = true;
                    break;
                }
            }

            if (!x0UsedAfter)
            {
                return "il2cpp_codegen_raise_exception";
            }
        }

        return null;
    }
}
