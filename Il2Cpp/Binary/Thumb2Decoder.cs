using System;
using System.Collections.Generic;
using Rosetta.Pipeline;

namespace Rosetta.Binary;

/// <summary>
/// Thumb2 instruction scanner for ARM32 (armeabi-v7a) IL2CPP binaries.
///
/// Provides:
///   - BL/BLX target scanning within Thumb2 method bodies
///   - Veneer (linker trampoline) following for Thumb2 MOVW/MOVT/ADD/BX stubs
///   - Thumb2 BL target decoding per ARMv7 Architecture Reference Manual §A8.8.25
///
/// ═══════════════════════════════════════════════════════════════════════════
/// INSTRUCTION ENCODING (ARMv7-M Architecture Reference Manual):
/// ═══════════════════════════════════════════════════════════════════════════
///
/// Thumb2 instructions are 16-bit or 32-bit (little-endian):
///   - 16-bit: hw0[15:11] &lt; 0b11101
///   - 32-bit: hw0[15:11] >= 0b11101, followed by hw1
///
/// BL (Thumb→Thumb): hw0[15:11]=11110, hw1[15:14]=11, hw1[12]=1
///   Encoding T1: offset = SignExtend(S:I1:I2:imm10:imm11:0, 25)
///   where I1 = NOT(J1 XOR S), I2 = NOT(J2 XOR S)
///
/// BLX (Thumb→ARM): hw0[15:11]=11110, hw1[15:14]=11, hw1[12]=0
///   Same offset encoding, but target aligned to 4 and switches to ARM mode.
///
/// ═══════════════════════════════════════════════════════════════════════════
/// VENEER PATTERN (Linker-generated Thumb2 trampolines):
/// ═══════════════════════════════════════════════════════════════════════════
///
/// When a BL target exceeds ±16MB (Thumb2 BL range), the linker generates
/// a veneer stub in the text section:
///   MOVW IP, #imm16_lo    ; IP = bottom 16 bits of offset
///   MOVT IP, #imm16_hi    ; IP |= top 16 bits of offset &lt;&lt; 16
///   ADD  IP, IP, PC        ; IP += PC (current instr + 4)
///   BX   IP                ; branch to final target (mode switch if bit0=0)
///
/// These are 16 bytes total (4x 32-bit Thumb2 instructions).
/// </summary>
public static class Thumb2Decoder
{
    /// <summary>
    /// Decode a Thumb2 BL/BLX instruction at the given offset in code.
    /// Returns the absolute target VA, or 0 on failure.
    /// </summary>
    public static uint DecodeBLTarget(ReadOnlySpan<byte> code, int offset, uint instrVA)
    {
        if (offset + 4 > code.Length) return 0;

        ushort hw0 = (ushort)(code[offset] | (code[offset + 1] << 8));
        ushort hw1 = (ushort)(code[offset + 2] | (code[offset + 3] << 8));

        // Verify this is BL or BLX
        if ((hw0 >> 11) != 0b11110) return 0;
        if ((hw1 >> 14) != 0b11) return 0;

        bool isBL = ((hw1 >> 12) & 1) == 1;  // BL if bit12=1, BLX if bit12=0

        int S = (hw0 >> 10) & 1;
        int imm10 = hw0 & 0x3FF;
        int J1 = (hw1 >> 13) & 1;
        int J2 = (hw1 >> 11) & 1;
        int imm11 = hw1 & 0x7FF;

        int I1 = (~(J1 ^ S)) & 1;
        int I2 = (~(J2 ^ S)) & 1;

        int rawOffset = (S << 24) | (I1 << 23) | (I2 << 22) | (imm10 << 12) | (imm11 << 1);

        // Sign-extend from 25 bits
        if ((rawOffset & (1 << 24)) != 0)
            rawOffset |= unchecked((int)0xFE000000);

        uint target = (uint)(instrVA + 4 + rawOffset);

        // BLX aligns target to 4-byte boundary
        if (!isBL)
            target &= ~3u;

        return target;
    }

    /// <summary>
    /// Check if the instruction at offset is a 32-bit Thumb2 instruction.
    /// </summary>
    public static bool Is32BitInstruction(ReadOnlySpan<byte> code, int offset)
    {
        if (offset + 2 > code.Length) return false;
        ushort hw0 = (ushort)(code[offset] | (code[offset + 1] << 8));
        return (hw0 >> 11) >= 0b11101;
    }

    /// <summary>
    /// Check if the instruction at offset is a BL or BLX.
    /// </summary>
    public static bool IsBLorBLX(ReadOnlySpan<byte> code, int offset)
    {
        if (offset + 4 > code.Length) return false;
        ushort hw0 = (ushort)(code[offset] | (code[offset + 1] << 8));
        ushort hw1 = (ushort)(code[offset + 2] | (code[offset + 3] << 8));
        return (hw0 >> 11) == 0b11110 && (hw1 >> 14) == 0b11;
    }

    /// <summary>
    /// Check if the instruction at offset is a BLX (Thumb→ARM mode switch).
    /// </summary>
    public static bool IsBLX(ReadOnlySpan<byte> code, int offset)
    {
        if (offset + 4 > code.Length) return false;
        ushort hw0 = (ushort)(code[offset] | (code[offset + 1] << 8));
        ushort hw1 = (ushort)(code[offset + 2] | (code[offset + 3] << 8));
        return (hw0 >> 11) == 0b11110 && (hw1 >> 14) == 0b11 && ((hw1 >> 12) & 1) == 0;
    }

    /// <summary>
    /// Scan a Thumb2 method body for all BL/BLX instructions.
    /// Returns a list of (instructionVA, rawTarget, resolvedTarget) tuples.
    ///
    /// If followVeneers is true, veneer trampolines are followed to get the
    /// final target address.
    /// </summary>
    public static List<(uint instrVA, uint rawTarget, uint resolvedTarget)> ScanBLTargets(
        ReadOnlySpan<byte> code,
        uint methodVA,
        ReadOnlyMemory<byte> binaryData,
        IBinaryParser elf,
        bool followVeneers = true)
    {
        var results = new List<(uint, uint, uint)>();
        int i = 0;

        while (i < code.Length - 2)
        {
            if (Is32BitInstruction(code, i))
            {
                if (i + 4 > code.Length) break;

                if (IsBLorBLX(code, i))
                {
                    uint instrVA = methodVA + (uint)i;
                    uint rawTarget = DecodeBLTarget(code, i, instrVA);

                    if (rawTarget != 0)
                    {
                        uint resolved = followVeneers
                            ? FollowVeneer(binaryData.Span, elf, rawTarget)
                            : rawTarget;
                        results.Add((instrVA, rawTarget, resolved));
                    }
                }
                i += 4;
            }
            else
            {
                i += 2;
            }
        }

        return results;
    }

    /// <summary>
    /// Follow a veneer (linker trampoline) to get the final target address.
    ///
    /// Thumb2 veneers use: MOVW IP, #lo; MOVT IP, #hi; ADD IP, IP, PC; BX IP
    /// ARM-mode veneers use: ADD IP, PC, #imm; ADD IP, IP, #imm; LDR PC, [IP, #imm]
    ///
    /// Returns the original address if no veneer pattern is detected.
    /// </summary>
    public static uint FollowVeneer(ReadOnlySpan<byte> data, IBinaryParser elf, uint targetVA, int maxDepth = 3)
    {
        uint current = targetVA;

        for (int depth = 0; depth < maxDepth; depth++)
        {
            long fileOffset = elf.VirtualToFileOffset(current);
            if (fileOffset < 0 || fileOffset + 16 > data.Length)
                return current;

            var chunk = data.Slice((int)fileOffset, 16);

            // ── Thumb2 veneer pattern ──
            // MOVW IP(R12), #imm16: 1111_0i10_0100_imm4 : 0_imm3_1100_imm8
            // hw0: F240-F64F range, hw1: 0Cxx
            ushort hw0 = (ushort)(chunk[0] | (chunk[1] << 8));
            ushort hw1 = (ushort)(chunk[2] | (chunk[3] << 8));

            bool isMovwIP = (hw0 & 0xFBF0) == 0xF240 && ((hw1 >> 8) & 0x0F) == 0x0C;

            if (isMovwIP && fileOffset + 16 <= data.Length)
            {
                // Decode MOVW immediate
                uint imm4_0 = (uint)(hw0 & 0xF);
                uint i_0 = (uint)((hw0 >> 10) & 1);
                uint imm3_0 = (uint)((hw1 >> 12) & 0x7);
                uint imm8_0 = (uint)(hw1 & 0xFF);
                uint movw_imm = (imm4_0 << 12) | (i_0 << 11) | (imm3_0 << 8) | imm8_0;

                // Check for MOVT IP
                ushort hw2 = (ushort)(chunk[4] | (chunk[5] << 8));
                ushort hw3 = (ushort)(chunk[6] | (chunk[7] << 8));

                bool isMovtIP = (hw2 & 0xFBF0) == 0xF2C0 && ((hw3 >> 8) & 0x0F) == 0x0C;
                if (!isMovtIP) return current;

                uint imm4_1 = (uint)(hw2 & 0xF);
                uint i_1 = (uint)((hw2 >> 10) & 1);
                uint imm3_1 = (uint)((hw3 >> 12) & 0x7);
                uint imm8_1 = (uint)(hw3 & 0xFF);
                uint movt_imm = (imm4_1 << 12) | (i_1 << 11) | (imm3_1 << 8) | imm8_1;

                uint fullImm = (movt_imm << 16) | movw_imm;

                // Check for ADD IP, IP, PC (or ADD IP, PC, IP)
                ushort hw4 = (ushort)(chunk[8] | (chunk[9] << 8));
                ushort hw5 = (ushort)(chunk[10] | (chunk[11] << 8));

                // ADD IP, IP, PC: EB0C 0F0C or ADD IP, PC, IP: EB0F 0C0C
                bool isAddIPPC = (hw4 == 0xEB0C && (hw5 & 0xFF0F) == 0x0F00) ||
                                 (hw4 == 0xEB0F && (hw5 & 0xFF0F) == 0x0C00);
                if (!isAddIPPC) return current;

                // Check for BX IP
                ushort hw6 = (ushort)(chunk[12] | (chunk[13] << 8));
                if (hw6 != 0x4760) return current; // BX IP = 0x4760

                // PC value at ADD instruction = veneer_VA + 8 + 4 (Thumb pipeline: current + 4)
                // Actually in Thumb mode, PC = instruction address + 4
                uint addVA = current + 8; // ADD is 3rd instruction (offset 8)
                uint pcAtAdd = addVA + 4; // Thumb PC = instr + 4
                uint finalTarget = fullImm + pcAtAdd;

                current = finalTarget;
                continue;
            }

            // No veneer pattern matched
            return current;
        }

        return current;
    }
}
