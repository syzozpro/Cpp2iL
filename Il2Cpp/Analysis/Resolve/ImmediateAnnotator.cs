using System;
using System.Globalization;
using Rosetta.Binary;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.Resolve;

/// <summary>
/// Annotates ARM64 MOVZ/MOVK/MOVN immediate values with their semantic meaning.
///
/// IL2CPP codegen patterns:
///   1. Integer constants: MOVZ Wd, #val, LSL #0
///      → annotation: = decimal_value
///
///   2. Float constants via upper-16 bits: MOVZ Wd, #bits, LSL #16
///      → The upper 16 bits are the IEEE754 sign+exponent+high mantissa,
///        with lower 16 bits implicitly zero.
///      → annotation: = float_value
///
///   3. Float constants via MOVZ+MOVK pair:
///      MOVZ Wd, #lo, LSL #0
///      MOVK Wd, #hi, LSL #16
///      → Combines to full 32-bit IEEE754 float.
///      → annotation on MOVK: = float_value
///
///   4. Negative integers: MOVN Wd, #val, LSL #0
///      → Result = ~(val << shift)
///      → annotation: = signed_decimal_value
///
/// Source: ARM Architecture Reference Manual C6.2.191 (MOVZ), C6.2.189 (MOVN)
/// </summary>
public sealed class ImmediateAnnotator
{
    // Track MOVZ state per register for MOVZ+MOVK float/double pairs
    private readonly ulong[] _accum = new ulong[32];     // accumulated value
    private readonly int[] _movzShift = new int[32];      // shift of initial MOVZ
    private readonly bool[] _is64Bit = new bool[32];      // was the MOVZ 64-bit (Xn)?
    private readonly bool[] _hasMOVZ = new bool[32];      // tracking active?
    private readonly int[] _movkCount = new int[32];      // how many MOVKs seen

    /// <summary>Reset tracking state (call at start of each method).</summary>
    public void Reset()
    {
        Array.Clear(_hasMOVZ);
        Array.Clear(_movkCount);
    }

    /// <summary>
    /// Try to produce an annotation string for a MOVZ/MOVK/MOVN instruction.
    /// Pass the next instruction (if available) to detect init-flag vs real bool patterns.
    ///
    /// Source evidence for init-flag pattern:
    ///   CodeWriterExtensions.cs:251 → s_Il2CppMethodInitialized = true
    ///   ARM64 pattern: MOVZ W8, #0x1 → STRB W8, [Xn, #off] (to .bss static)
    /// </summary>
    public string? Annotate(in Arm64Instruction inst, in Arm64Instruction? nextInst = null)
    {
        switch (inst.Opcode)
        {
            case Arm64Opcode.MOVZ:
                return AnnotateMOVZ(inst, nextInst);
            case Arm64Opcode.MOVK:
                return AnnotateMOVK(inst);
            case Arm64Opcode.MOVN:
                return AnnotateMOVN(inst);
            default:
                InvalidateIfWritesRd(inst);
                return null;
        }
    }

    /// <summary>
    /// Check if a MOVZ #0x1 is an IL2CPP init flag store.
    /// Pattern: MOVZ Wd, #0x1 followed by STRB Wd, [Xn, #off]
    /// where Xn holds ADRP page to .data/.bss (NOT SP).
    /// Source: CodeWriterExtensions.cs:258 → Emit.Assign("s_Il2CppMethodInitialized", "true")
    /// </summary>
    private static bool IsInitFlagStore(in Arm64Instruction movz, in Arm64Instruction? nextInst)
    {
        if (nextInst == null) return false;
        var next = nextInst.Value;
        // Must be STRB to same register, with base NOT SP (reg 31)
        if (next.Opcode != Arm64Opcode.STRB_IMM) return false;
        if (next.Rd != movz.Rd) return false;
        if (next.Rn == 31) return false; // SP = stack = real value, not init flag
        return true;
    }

    private string? AnnotateMOVZ(in Arm64Instruction inst, in Arm64Instruction? nextInst)
    {
        ushort imm = (ushort)inst.Immediate;
        int shift = inst.Shift;
        int rd = inst.Rd;

        // Track for potential MOVK follow-up
        if (rd < 32)
        {
            _accum[rd] = (ulong)imm << shift;
            _movzShift[rd] = shift;
            _is64Bit[rd] = inst.Is64Bit;
            _hasMOVZ[rd] = true;
            _movkCount[rd] = 0;
        }

        if (shift == 0)
        {
            // Skip init-flag stores: MOVZ #1 → STRB to .bss (not SP)
            // Source: CodeWriterExtensions.cs:258 → s_Il2CppMethodInitialized = true
            if (imm == 1 && IsInitFlagStore(inst, nextInst))
                return null;

            // Integer constant (could be int, bool, byte, enum — no type info here)
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      ImmAnnotator: MOVZ #{imm} → = {imm}");
            return $"= {imm}";
        }

        if (shift == 16 && !inst.Is64Bit)
        {
            // MOVZ Wd, #val, LSL #16 → upper-16 float with zero lower bits
            uint bits = (uint)imm << 16;
            float val = BitConverter.Int32BitsToSingle((int)bits);
            if (float.IsNormal(val) || val == 0f)
                return $"= {val.ToString("G", CultureInfo.InvariantCulture)}f";
        }

        return null;
    }

    private string? AnnotateMOVK(in Arm64Instruction inst)
    {
        ushort imm = (ushort)inst.Immediate;
        int shift = inst.Shift;
        int rd = inst.Rd;

        if (rd >= 32 || !_hasMOVZ[rd])
        {
            if (rd < 32) _hasMOVZ[rd] = false;
            return null;
        }

        // Merge this MOVK into the accumulator
        ulong mask = ~(0xFFFFUL << shift);
        _accum[rd] = (_accum[rd] & mask) | ((ulong)imm << shift);
        _movkCount[rd]++;

        // 32-bit float: MOVZ Wd + MOVK Wd LSL#16 (2 instructions total)
        if (!_is64Bit[rd] && shift == 16 && _movzShift[rd] == 0)
        {
            uint bits = (uint)_accum[rd];
            float val = BitConverter.Int32BitsToSingle((int)bits);
            _hasMOVZ[rd] = false;
            if (float.IsNormal(val) || val == 0f)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      ImmAnnotator: MOVZ+MOVK float → {val}f (bits=0x{bits:X8})");
                return $"= {val.ToString("G", CultureInfo.InvariantCulture)}f";
            }
        }

        // 64-bit double: needs all 4 slots filled (MOVZ + 3x MOVK)
        if (_is64Bit[rd] && _movkCount[rd] >= 3 && shift == 48)
        {
            ulong bits = _accum[rd];
            double val = BitConverter.Int64BitsToDouble((long)bits);
            _hasMOVZ[rd] = false;
            if (double.IsNormal(val) || val == 0.0)
                return $"= {val.ToString("G", CultureInfo.InvariantCulture)}d";
        }

        // 64-bit integer: MOVZ Xd + MOVK Xd LSL#16 (2 instructions, large int)
        if (_is64Bit[rd] && _movkCount[rd] >= 1 && shift == 48)
        {
            long sval = (long)_accum[rd];
            _hasMOVZ[rd] = false;
            return $"= {sval}L";
        }

        // Keep tracking — more MOVKs may follow
        return null;
    }

    private string? AnnotateMOVN(in Arm64Instruction inst)
    {
        ushort imm = (ushort)inst.Immediate;
        int shift = inst.Shift;
        int rd = inst.Rd;

        if (rd < 32)
            _hasMOVZ[rd] = false;

        if (shift == 0 && !inst.Is64Bit)
        {
            // MOVN Wd, #val, LSL #0 → result = ~val (32-bit)
            int result = ~(int)imm;
            return $"= {result}";
        }

        if (shift == 0 && inst.Is64Bit)
        {
            // MOVN Xd, #val, LSL #0 → result = ~val (64-bit)
            long result = ~(long)imm;
            return $"= {result}";
        }

        return null;
    }

    private void InvalidateIfWritesRd(in Arm64Instruction inst)
    {
        int rd = inst.Rd;
        if (rd < 31 && Rosetta.Analysis.Utils.ArmUtils.IsWriteToRd(inst.Opcode))
            _hasMOVZ[rd] = false;
    }


}
