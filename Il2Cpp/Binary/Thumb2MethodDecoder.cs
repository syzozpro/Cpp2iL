using System;

namespace Rosetta.Binary;

/// <summary>
/// Decodes ARM32 Thumb2 method bodies into Thumb2Instruction arrays.
///
/// Source: ARMv7-M Architecture Reference Manual (DDI 0403)
///         §A6.1 Thumb instruction set encoding
///
/// Thumb2 uses mixed 16-bit and 32-bit instructions:
///   - 16-bit: hw0[15:11] < 0b11101
///   - 32-bit: hw0[15:11] >= 0b11101 (hw0 is first halfword, hw1 is second)
///
/// All instructions are little-endian. For 32-bit instructions, hw0 comes first
/// in memory (lower address), hw1 comes second (higher address).
/// </summary>
public static class Thumb2MethodDecoder
{
    /// <summary>
    /// Decode a block of Thumb2 code starting at the given VA.
    /// Returns the decoded instruction array.
    /// </summary>
    public static Thumb2Instruction[] DecodeBlock(ReadOnlySpan<byte> code, ulong baseVA, int maxInstructions = 4096)
    {
        var result = new Thumb2Instruction[Math.Min(maxInstructions, code.Length / 2)];
        int count = 0;
        int pos = 0;

        while (pos < code.Length - 1 && count < result.Length)
        {
            ulong instrVA = baseVA + (uint)pos;
            ushort hw0 = (ushort)(code[pos] | (code[pos + 1] << 8));

            if ((hw0 >> 11) >= 0b11101)
            {
                // 32-bit instruction
                if (pos + 4 > code.Length) break;
                ushort hw1 = (ushort)(code[pos + 2] | (code[pos + 3] << 8));
                uint raw32 = (uint)hw0 | ((uint)hw1 << 16);
                result[count++] = Decode32(raw32, hw0, hw1, instrVA);
                pos += 4;
            }
            else
            {
                // 16-bit instruction
                result[count++] = Decode16(hw0, instrVA);
                pos += 2;
            }
        }

        if (count < result.Length)
            return result[..count];
        return result;
    }

    // ════════════════════════════════════════════════════════════════════
    // 16-bit Thumb instruction decoder
    // ════════════════════════════════════════════════════════════════════

    private static Thumb2Instruction Decode16(ushort hw, ulong va)
    {
        // ── PUSH: 1011_010M_RRRR_RRRR ──
        // M=1 includes LR. Register list in bottom 8 bits.
        if ((hw & 0xFE00) == 0xB400)
        {
            ushort regList = (ushort)(hw & 0xFF);
            if ((hw & 0x0100) != 0) regList |= (1 << 14); // LR
            return new(hw, va, Thumb2Opcode.PUSH, size: 2, registerList: regList);
        }

        // ── POP: 1011_110P_RRRR_RRRR ──
        // P=1 includes PC. Register list in bottom 8 bits.
        if ((hw & 0xFE00) == 0xBC00)
        {
            ushort regList = (ushort)(hw & 0xFF);
            if ((hw & 0x0100) != 0) regList |= (1 << 15); // PC
            return new(hw, va, Thumb2Opcode.POP, size: 2, registerList: regList);
        }

        // ── MOV Rd, #imm8: 0010_0ddd_iiii_iiii ──
        if ((hw >> 11) == 0b00100)
        {
            byte rd = (byte)((hw >> 8) & 0x7);
            int imm8 = hw & 0xFF;
            return new(hw, va, Thumb2Opcode.MOV_IMM, rd: rd, immediate: imm8, size: 2);
        }

        // ── MOV Rd, Rm (high registers): 0100_0110_D_Rmmm_Rddd ──
        if ((hw & 0xFF00) == 0x4600)
        {
            int rm = (hw >> 3) & 0xF;
            int rd = ((hw >> 4) & 0x8) | (hw & 0x7);
            return new(hw, va, Thumb2Opcode.MOV_REG, rd: (byte)rd, rm: (byte)rm, size: 2);
        }

        // ── ADD Rd, Rn, #imm3: 0001_110i_iinn_nddd ──
        if ((hw >> 9) == 0b0001110)
        {
            byte rd = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            int imm3 = (hw >> 6) & 0x7;
            return new(hw, va, Thumb2Opcode.ADD_IMM, rd: rd, rn: rn, immediate: imm3, size: 2);
        }

        // ── ADD Rd, Rd, #imm8: 0011_0ddd_iiii_iiii ──
        if ((hw >> 11) == 0b00110)
        {
            byte rd = (byte)((hw >> 8) & 0x7);
            int imm8 = hw & 0xFF;
            return new(hw, va, Thumb2Opcode.ADD_IMM, rd: rd, rn: rd, immediate: imm8, size: 2);
        }

        // ── ADD Rd, Rn, Rm: 0001_100m_mmnn_nddd ──
        if ((hw >> 9) == 0b0001100)
        {
            byte rd = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            byte rm = (byte)((hw >> 6) & 0x7);
            return new(hw, va, Thumb2Opcode.ADD_REG, rd: rd, rn: rn, rm: rm, size: 2);
        }

        // ── ADD Rd, SP, #imm8*4: 1010_1ddd_iiii_iiii ──
        if ((hw >> 11) == 0b10101)
        {
            byte rd = (byte)((hw >> 8) & 0x7);
            int imm8 = (hw & 0xFF) * 4;
            return new(hw, va, Thumb2Opcode.ADD_IMM, rd: rd, rn: 13, immediate: imm8, size: 2);
        }

        // ── ADD SP, SP, #imm7*4: 1011_0000_0iii_iiii ──
        if ((hw & 0xFF80) == 0xB000)
        {
            int imm7 = (hw & 0x7F) * 4;
            return new(hw, va, Thumb2Opcode.ADD_IMM, rd: 13, rn: 13, immediate: imm7, size: 2);
        }

        // ── ADD Rd, Rm (high): 0100_0100_Dmm_mRdd ──
        if ((hw & 0xFF00) == 0x4400)
        {
            int rd = ((hw >> 4) & 0x8) | (hw & 0x7);
            int rm = (hw >> 3) & 0xF;
            return new(hw, va, Thumb2Opcode.ADD_REG, rd: (byte)rd, rn: (byte)rd, rm: (byte)rm, size: 2);
        }

        // ── SUB Rd, Rn, #imm3: 0001_111i_iinn_nddd ──
        if ((hw >> 9) == 0b0001111)
        {
            byte rd = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            int imm3 = (hw >> 6) & 0x7;
            return new(hw, va, Thumb2Opcode.SUB_IMM, rd: rd, rn: rn, immediate: imm3, size: 2);
        }

        // ── SUB Rd, Rd, #imm8: 0011_1ddd_iiii_iiii ──
        if ((hw >> 11) == 0b00111)
        {
            byte rd = (byte)((hw >> 8) & 0x7);
            int imm8 = hw & 0xFF;
            return new(hw, va, Thumb2Opcode.SUB_IMM, rd: rd, rn: rd, immediate: imm8, size: 2);
        }

        // ── SUB Rd, Rn, Rm: 0001_101m_mmnn_nddd ──
        if ((hw >> 9) == 0b0001101)
        {
            byte rd = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            byte rm = (byte)((hw >> 6) & 0x7);
            return new(hw, va, Thumb2Opcode.SUB_REG, rd: rd, rn: rn, rm: rm, size: 2);
        }

        // ── SUB SP, SP, #imm7*4: 1011_0000_1iii_iiii ──
        if ((hw & 0xFF80) == 0xB080)
        {
            int imm7 = (hw & 0x7F) * 4;
            return new(hw, va, Thumb2Opcode.SUB_IMM, rd: 13, rn: 13, immediate: imm7, size: 2);
        }

        // ── CMP Rn, #imm8: 0010_1nnn_iiii_iiii ──
        if ((hw >> 11) == 0b00101)
        {
            byte rn = (byte)((hw >> 8) & 0x7);
            int imm8 = hw & 0xFF;
            return new(hw, va, Thumb2Opcode.CMP_IMM, rn: rn, immediate: imm8, size: 2);
        }

        // ── CMP Rn, Rm (high): 0100_0101_Nmm_mRnn ──
        if ((hw & 0xFF00) == 0x4500)
        {
            int rn = ((hw >> 4) & 0x8) | (hw & 0x7);
            int rm = (hw >> 3) & 0xF;
            return new(hw, va, Thumb2Opcode.CMP_REG, rn: (byte)rn, rm: (byte)rm, size: 2);
        }

        // ── CMP Rn, Rm (low): 0100_0010_10mm_mnnn ──
        if ((hw & 0xFFC0) == 0x4280)
        {
            byte rn = (byte)(hw & 0x7);
            byte rm = (byte)((hw >> 3) & 0x7);
            return new(hw, va, Thumb2Opcode.CMP_REG, rn: rn, rm: rm, size: 2);
        }

        // ── Data processing (low regs): 0100_00xx_xxmm_mddd ──
        if ((hw >> 10) == 0b010000)
        {
            int op = (hw >> 6) & 0xF;
            byte rd = (byte)(hw & 0x7);
            byte rm = (byte)((hw >> 3) & 0x7);
            return op switch
            {
                0b0000 => new(hw, va, Thumb2Opcode.AND_REG, rd: rd, rn: rd, rm: rm, size: 2),
                0b0001 => new(hw, va, Thumb2Opcode.EOR_REG, rd: rd, rn: rd, rm: rm, size: 2),
                0b0010 => new(hw, va, Thumb2Opcode.LSL_REG, rd: rd, rn: rd, rm: rm, size: 2),
                0b0011 => new(hw, va, Thumb2Opcode.LSR_REG, rd: rd, rn: rd, rm: rm, size: 2),
                0b0100 => new(hw, va, Thumb2Opcode.ASR_REG, rd: rd, rn: rd, rm: rm, size: 2),
                0b0111 => new(hw, va, Thumb2Opcode.Unknown, size: 2), // ROR — rarely used
                0b1000 => new(hw, va, Thumb2Opcode.TST_REG, rn: rd, rm: rm, size: 2),
                0b1001 => new(hw, va, Thumb2Opcode.NEG, rd: rd, rm: rm, size: 2), // RSB Rd, Rm, #0
                0b1010 => new(hw, va, Thumb2Opcode.CMP_REG, rn: rd, rm: rm, size: 2),
                0b1100 => new(hw, va, Thumb2Opcode.ORR_REG, rd: rd, rn: rd, rm: rm, size: 2),
                0b1101 => new(hw, va, Thumb2Opcode.MUL, rd: rd, rn: rm, rm: rd, size: 2),
                0b1110 => new(hw, va, Thumb2Opcode.BIC_REG, rd: rd, rn: rd, rm: rm, size: 2),
                0b1111 => new(hw, va, Thumb2Opcode.MVN_REG, rd: rd, rm: rm, size: 2),
                _ => new(hw, va, Thumb2Opcode.Unknown, size: 2),
            };
        }

        // ── LDR Rt, [Rn, #imm5*4]: 0110_1iii_iinn_nttt ──
        if ((hw >> 11) == 0b01101)
        {
            byte rt = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            int imm5 = ((hw >> 6) & 0x1F) * 4;
            return new(hw, va, Thumb2Opcode.LDR_IMM, rd: rt, rn: rn, immediate: imm5, size: 2);
        }

        // ── STR Rt, [Rn, #imm5*4]: 0110_0iii_iinn_nttt ──
        if ((hw >> 11) == 0b01100)
        {
            byte rt = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            int imm5 = ((hw >> 6) & 0x1F) * 4;
            return new(hw, va, Thumb2Opcode.STR_IMM, rd: rt, rn: rn, immediate: imm5, size: 2);
        }

        // ── LDRB Rt, [Rn, #imm5]: 0111_1iii_iinn_nttt ──
        if ((hw >> 11) == 0b01111)
        {
            byte rt = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            int imm5 = (hw >> 6) & 0x1F;
            return new(hw, va, Thumb2Opcode.LDRB_IMM, rd: rt, rn: rn, immediate: imm5, size: 2);
        }

        // ── STRB Rt, [Rn, #imm5]: 0111_0iii_iinn_nttt ──
        if ((hw >> 11) == 0b01110)
        {
            byte rt = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            int imm5 = (hw >> 6) & 0x1F;
            return new(hw, va, Thumb2Opcode.STRB_IMM, rd: rt, rn: rn, immediate: imm5, size: 2);
        }

        // ── LDRH Rt, [Rn, #imm5*2]: 1000_1iii_iinn_nttt ──
        if ((hw >> 11) == 0b10001)
        {
            byte rt = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            int imm5 = ((hw >> 6) & 0x1F) * 2;
            return new(hw, va, Thumb2Opcode.LDRH_IMM, rd: rt, rn: rn, immediate: imm5, size: 2);
        }

        // ── STRH Rt, [Rn, #imm5*2]: 1000_0iii_iinn_nttt ──
        if ((hw >> 11) == 0b10000)
        {
            byte rt = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            int imm5 = ((hw >> 6) & 0x1F) * 2;
            return new(hw, va, Thumb2Opcode.STRH_IMM, rd: rt, rn: rn, immediate: imm5, size: 2);
        }

        // ── LDR Rt, [SP, #imm8*4]: 1001_1ttt_iiii_iiii ──
        if ((hw >> 11) == 0b10011)
        {
            byte rt = (byte)((hw >> 8) & 0x7);
            int imm8 = (hw & 0xFF) * 4;
            return new(hw, va, Thumb2Opcode.LDR_IMM, rd: rt, rn: 13, immediate: imm8, size: 2);
        }

        // ── STR Rt, [SP, #imm8*4]: 1001_0ttt_iiii_iiii ──
        if ((hw >> 11) == 0b10010)
        {
            byte rt = (byte)((hw >> 8) & 0x7);
            int imm8 = (hw & 0xFF) * 4;
            return new(hw, va, Thumb2Opcode.STR_IMM, rd: rt, rn: 13, immediate: imm8, size: 2);
        }

        // ── LDR Rt, [Rn, Rm]: 0101_100m_mmnn_nttt ──
        if ((hw >> 9) == 0b0101100)
        {
            byte rt = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            byte rm = (byte)((hw >> 6) & 0x7);
            return new(hw, va, Thumb2Opcode.LDR_REG, rd: rt, rn: rn, rm: rm, size: 2);
        }

        // ── STR Rt, [Rn, Rm]: 0101_000m_mmnn_nttt ──
        if ((hw >> 9) == 0b0101000)
        {
            byte rt = (byte)(hw & 0x7);
            byte rn = (byte)((hw >> 3) & 0x7);
            byte rm = (byte)((hw >> 6) & 0x7);
            return new(hw, va, Thumb2Opcode.STR_REG, rd: rt, rn: rn, rm: rm, size: 2);
        }

        // ── LDR Rt, [PC, #imm8*4]: 0100_1ttt_iiii_iiii ──
        if ((hw >> 11) == 0b01001)
        {
            byte rt = (byte)((hw >> 8) & 0x7);
            int imm8 = (hw & 0xFF) * 4;
            // PC-relative: target = Align(PC, 4) + imm
            // In Thumb, PC = instruction address + 4
            ulong pc = (va + 4) & ~3UL;
            long target = (long)(pc + (ulong)imm8);
            return new(hw, va, Thumb2Opcode.LDR_LIT, rd: rt, immediate: target, size: 2);
        }

        // ── LSL Rd, Rm, #imm5: 0000_0iii_iimm_mddd ──
        if ((hw >> 11) == 0b00000 && ((hw >> 6) & 0x1F) != 0)
        {
            byte rd = (byte)(hw & 0x7);
            byte rm = (byte)((hw >> 3) & 0x7);
            int imm5 = (hw >> 6) & 0x1F;
            return new(hw, va, Thumb2Opcode.LSL_IMM, rd: rd, rn: rm, immediate: imm5, size: 2);
        }

        // ── LSR Rd, Rm, #imm5: 0000_1iii_iimm_mddd ──
        if ((hw >> 11) == 0b00001)
        {
            byte rd = (byte)(hw & 0x7);
            byte rm = (byte)((hw >> 3) & 0x7);
            int imm5 = (hw >> 6) & 0x1F;
            if (imm5 == 0) imm5 = 32;
            return new(hw, va, Thumb2Opcode.LSR_IMM, rd: rd, rn: rm, immediate: imm5, size: 2);
        }

        // ── ASR Rd, Rm, #imm5: 0001_0iii_iimm_mddd ──
        if ((hw >> 11) == 0b00010)
        {
            byte rd = (byte)(hw & 0x7);
            byte rm = (byte)((hw >> 3) & 0x7);
            int imm5 = (hw >> 6) & 0x1F;
            if (imm5 == 0) imm5 = 32;
            return new(hw, va, Thumb2Opcode.ASR_IMM, rd: rd, rn: rm, immediate: imm5, size: 2);
        }

        // ── CBZ/CBNZ: 1011_x0i1_iiii_innn ──
        // CBZ:  (hw & 0xF500) == 0xB100
        // CBNZ: (hw & 0xF500) == 0xB900
        if ((hw & 0xF500) == 0xB100)
        {
            byte rn = (byte)(hw & 0x7);
            int i = ((hw >> 9) & 1) << 5;
            int imm5 = ((hw >> 3) & 0x1F) << 1;
            ulong target = va + 4 + (ulong)(i | imm5);
            return new(hw, va, Thumb2Opcode.CBZ, rd: rn, immediate: (long)target, size: 2);
        }
        if ((hw & 0xF500) == 0xB900)
        {
            byte rn = (byte)(hw & 0x7);
            int i = ((hw >> 9) & 1) << 5;
            int imm5 = ((hw >> 3) & 0x1F) << 1;
            ulong target = va + 4 + (ulong)(i | imm5);
            return new(hw, va, Thumb2Opcode.CBNZ, rd: rn, immediate: (long)target, size: 2);
        }

        // ── B<cond>: 1101_cccc_iiii_iiii ──
        if ((hw >> 12) == 0b1101 && ((hw >> 8) & 0xF) < 0xE)
        {
            byte cond = (byte)((hw >> 8) & 0xF);
            int imm8 = hw & 0xFF;
            // Sign-extend from 9 bits (imm8 << 1)
            int offset = imm8 << 1;
            if ((offset & (1 << 8)) != 0) offset |= unchecked((int)0xFFFFFE00);
            long target = (long)(va + 4) + offset;
            return new(hw, va, Thumb2Opcode.B_COND, condition: cond, immediate: target, size: 2);
        }

        // ── B (unconditional): 1110_0iii_iiii_iiii ──
        if ((hw >> 11) == 0b11100)
        {
            int imm11 = hw & 0x7FF;
            int offset = imm11 << 1;
            if ((offset & (1 << 11)) != 0) offset |= unchecked((int)0xFFFFF000);
            long target = (long)(va + 4) + offset;
            return new(hw, va, Thumb2Opcode.B, immediate: target, size: 2);
        }

        // ── BX Rm: 0100_0111_0mmm_m000 ──
        if ((hw & 0xFF87) == 0x4700)
        {
            byte rm = (byte)((hw >> 3) & 0xF);
            return new(hw, va, Thumb2Opcode.BX, rm: rm, size: 2);
        }

        // ── ADD Rd, PC, #imm8*4: 1010_0ddd_iiii_iiii ──
        if ((hw >> 11) == 0b10100)
        {
            byte rd = (byte)((hw >> 8) & 0x7);
            int imm8 = (hw & 0xFF) * 4;
            ulong pc = (va + 4) & ~3UL;
            return new(hw, va, Thumb2Opcode.ADD_IMM, rd: rd, rn: 15, immediate: (long)(pc + (ulong)imm8), size: 2);
        }

        // ── NOP: 1011_1111_0000_0000 ──
        if (hw == 0xBF00)
            return new(hw, va, Thumb2Opcode.NOP, size: 2);

        // ── IT: 1011_1111_cccc_mmmm (mask != 0) ──
        if ((hw & 0xFF00) == 0xBF00 && (hw & 0xF) != 0)
        {
            byte cond = (byte)((hw >> 4) & 0xF);
            int mask = hw & 0xF;
            // Count how many instructions in the IT block (1-4)
            byte count = 1;
            if ((mask & 0x1) != 0) count = 4;
            else if ((mask & 0x2) != 0) count = 3;
            else if ((mask & 0x4) != 0) count = 2;
            return new(hw, va, Thumb2Opcode.IT, condition: cond, shift: count, size: 2);
        }

        // ── Load/Store register offset variants (LDRB, LDRH, LDRSB, LDRSH, STRB, STRH) ──
        if ((hw >> 9) == 0b0101001) // LDRH Rt, [Rn, Rm]
        {
            byte rt = (byte)(hw & 0x7), rn = (byte)((hw >> 3) & 0x7), rm = (byte)((hw >> 6) & 0x7);
            return new(hw, va, Thumb2Opcode.LDRH_IMM, rd: rt, rn: rn, rm: rm, immediate: 0, size: 2);
        }
        if ((hw >> 9) == 0b0101011) // LDRSB Rt, [Rn, Rm]
        {
            byte rt = (byte)(hw & 0x7), rn = (byte)((hw >> 3) & 0x7), rm = (byte)((hw >> 6) & 0x7);
            return new(hw, va, Thumb2Opcode.LDRSB_IMM, rd: rt, rn: rn, rm: rm, immediate: 0, size: 2);
        }
        if ((hw >> 9) == 0b0101111) // LDRSH Rt, [Rn, Rm]
        {
            byte rt = (byte)(hw & 0x7), rn = (byte)((hw >> 3) & 0x7), rm = (byte)((hw >> 6) & 0x7);
            return new(hw, va, Thumb2Opcode.LDRSH_IMM, rd: rt, rn: rn, rm: rm, immediate: 0, size: 2);
        }
        if ((hw >> 9) == 0b0101010) // STRH Rt, [Rn, Rm]
        {
            byte rt = (byte)(hw & 0x7), rn = (byte)((hw >> 3) & 0x7), rm = (byte)((hw >> 6) & 0x7);
            return new(hw, va, Thumb2Opcode.STRH_IMM, rd: rt, rn: rn, rm: rm, immediate: 0, size: 2);
        }
        if ((hw >> 9) == 0b0101110) // LDRB Rt, [Rn, Rm]
        {
            byte rt = (byte)(hw & 0x7), rn = (byte)((hw >> 3) & 0x7), rm = (byte)((hw >> 6) & 0x7);
            return new(hw, va, Thumb2Opcode.LDRB_IMM, rd: rt, rn: rn, rm: rm, immediate: 0, size: 2);
        }
        if ((hw >> 9) == 0b0101100) // unused but could be STRB
        {
            byte rt = (byte)(hw & 0x7), rn = (byte)((hw >> 3) & 0x7), rm = (byte)((hw >> 6) & 0x7);
            return new(hw, va, Thumb2Opcode.STRB_IMM, rd: rt, rn: rn, rm: rm, immediate: 0, size: 2);
        }

        // ── MOV Rd, Rm (low): 0000_0000_00mm_mddd (LSL #0 alias) ──
        if ((hw >> 11) == 0b00000 && ((hw >> 6) & 0x1F) == 0)
        {
            byte rd = (byte)(hw & 0x7);
            byte rm = (byte)((hw >> 3) & 0x7);
            return new(hw, va, Thumb2Opcode.MOV_REG, rd: rd, rm: rm, size: 2);
        }

        return new(hw, va, Thumb2Opcode.Unknown, size: 2);
    }

    // ════════════════════════════════════════════════════════════════════
    // 32-bit Thumb2 instruction decoder
    // ════════════════════════════════════════════════════════════════════

    private static Thumb2Instruction Decode32(uint raw, ushort hw0, ushort hw1, ulong va)
    {
        // ── BL (Thumb→Thumb): hw0[15:11]=11110, hw1[15:14]=11, hw1[12]=1 ──
        if ((hw0 >> 11) == 0b11110 && (hw1 >> 14) == 0b11 && ((hw1 >> 12) & 1) == 1)
        {
            long target = DecodeBLTarget(hw0, hw1, va);
            return new(raw, va, Thumb2Opcode.BL, immediate: target, size: 4);
        }

        // ── BLX (Thumb→ARM): hw0[15:11]=11110, hw1[15:14]=11, hw1[12]=0 ──
        if ((hw0 >> 11) == 0b11110 && (hw1 >> 14) == 0b11 && ((hw1 >> 12) & 1) == 0)
        {
            long target = DecodeBLTarget(hw0, hw1, va);
            target &= ~3; // Align to 4
            return new(raw, va, Thumb2Opcode.BLX, immediate: target, size: 4);
        }

        // ── B.W conditional: 1111_0Scc_ccii_iiii 10j0_jiii_iiii_iiii ──
        if ((hw0 >> 11) == 0b11110 && (hw1 & 0xD000) == 0x8000)
        {
            byte cond = (byte)((hw0 >> 6) & 0xF);
            int S = (hw0 >> 10) & 1;
            int imm6 = hw0 & 0x3F;
            int J1 = (hw1 >> 13) & 1;
            int J2 = (hw1 >> 11) & 1;
            int imm11 = hw1 & 0x7FF;
            int offset = (S << 20) | (J2 << 19) | (J1 << 18) | (imm6 << 12) | (imm11 << 1);
            if (S != 0) offset |= unchecked((int)0xFFE00000); // sign-extend from 21 bits
            long target = (long)(va + 4) + offset;
            return new(raw, va, Thumb2Opcode.B_COND, condition: cond, immediate: target, size: 4);
        }

        // ── B.W unconditional: 1111_0Sii_iiii_iiii 10j1_jiii_iiii_iiii ──
        if ((hw0 >> 11) == 0b11110 && (hw1 & 0xD000) == 0x9000)
        {
            int S = (hw0 >> 10) & 1;
            int imm10 = hw0 & 0x3FF;
            int J1 = (hw1 >> 13) & 1;
            int J2 = (hw1 >> 11) & 1;
            int imm11 = hw1 & 0x7FF;
            int I1 = (~(J1 ^ S)) & 1;
            int I2 = (~(J2 ^ S)) & 1;
            int offset = (S << 24) | (I1 << 23) | (I2 << 22) | (imm10 << 12) | (imm11 << 1);
            if (S != 0) offset |= unchecked((int)0xFE000000); // sign-extend from 25 bits
            long target = (long)(va + 4) + offset;
            return new(raw, va, Thumb2Opcode.B, immediate: target, size: 4);
        }

        // ── PUSH.W: E92D xxxx ──
        if (hw0 == 0xE92D)
        {
            return new(raw, va, Thumb2Opcode.PUSH, size: 4, registerList: hw1);
        }

        // ── POP.W: E8BD xxxx ──
        if (hw0 == 0xE8BD)
        {
            return new(raw, va, Thumb2Opcode.POP, size: 4, registerList: hw1);
        }

        // ── MOVW: 1111_0i10_0100_iiii 0iii_dddd_iiii_iiii ──
        if ((hw0 & 0xFBF0) == 0xF240 && (hw1 & 0x8000) == 0)
        {
            int imm4 = hw0 & 0xF;
            int i = (hw0 >> 10) & 1;
            int imm3 = (hw1 >> 12) & 0x7;
            int imm8 = hw1 & 0xFF;
            int rd = (hw1 >> 8) & 0xF;
            int imm16 = (imm4 << 12) | (i << 11) | (imm3 << 8) | imm8;
            return new(raw, va, Thumb2Opcode.MOVW, rd: (byte)rd, immediate: imm16, size: 4);
        }

        // ── MOVT: 1111_0i10_1100_iiii 0iii_dddd_iiii_iiii ──
        if ((hw0 & 0xFBF0) == 0xF2C0 && (hw1 & 0x8000) == 0)
        {
            int imm4 = hw0 & 0xF;
            int i = (hw0 >> 10) & 1;
            int imm3 = (hw1 >> 12) & 0x7;
            int imm8 = hw1 & 0xFF;
            int rd = (hw1 >> 8) & 0xF;
            int imm16 = (imm4 << 12) | (i << 11) | (imm3 << 8) | imm8;
            return new(raw, va, Thumb2Opcode.MOVT, rd: (byte)rd, immediate: imm16, size: 4);
        }

        // ── LDR.W Rt, [Rn, #imm12]: 1111_1000_1101_nnnn tttt_iiii_iiii_iiii ──
        if ((hw0 & 0xFFF0) == 0xF8D0)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int imm12 = hw1 & 0xFFF;
            return new(raw, va, Thumb2Opcode.LDR_IMM, rd: (byte)rt, rn: (byte)rn, immediate: imm12, size: 4);
        }

        // ── LDR.W Rt, [Rn, #-imm8] or LDR.W Rt, [Rn, #imm8]{!}: F850 nnnn 1Puw iiiiiiii ──
        if ((hw0 & 0xFFF0) == 0xF850 && (hw1 & 0x0800) != 0)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int imm8 = hw1 & 0xFF;
            bool up = (hw1 & 0x0200) != 0;
            int offset = up ? imm8 : -imm8;
            return new(raw, va, Thumb2Opcode.LDR_IMM, rd: (byte)rt, rn: (byte)rn, immediate: offset, size: 4);
        }

        // ── LDR.W Rt, [Rn, Rm, LSL #sh]: F850 nnnn 0000 00sh mmmm ──
        if ((hw0 & 0xFFF0) == 0xF850 && (hw1 & 0x0FC0) == 0x0000)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int rm = hw1 & 0xF;
            int sh = (hw1 >> 4) & 0x3;
            return new(raw, va, Thumb2Opcode.LDR_REG, rd: (byte)rt, rn: (byte)rn, rm: (byte)rm, shift: (byte)sh, size: 4);
        }

        // ── STR.W Rt, [Rn, #imm12]: F8C0 nnnn tttt iiiiiiiiiiii ──
        if ((hw0 & 0xFFF0) == 0xF8C0)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int imm12 = hw1 & 0xFFF;
            return new(raw, va, Thumb2Opcode.STR_IMM, rd: (byte)rt, rn: (byte)rn, immediate: imm12, size: 4);
        }

        // ── STR.W Rt, [Rn, #-imm8]: F840 nnnn 1Puw iiiiiiii ──
        if ((hw0 & 0xFFF0) == 0xF840 && (hw1 & 0x0800) != 0)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int imm8 = hw1 & 0xFF;
            bool up = (hw1 & 0x0200) != 0;
            int offset = up ? imm8 : -imm8;
            return new(raw, va, Thumb2Opcode.STR_IMM, rd: (byte)rt, rn: (byte)rn, immediate: offset, size: 4);
        }

        // ── STR.W Rt, [Rn, Rm, LSL #sh]: F840 nnnn 0000 00sh mmmm ──
        if ((hw0 & 0xFFF0) == 0xF840 && (hw1 & 0x0FC0) == 0x0000)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int rm = hw1 & 0xF;
            int sh = (hw1 >> 4) & 0x3;
            return new(raw, va, Thumb2Opcode.STR_REG, rd: (byte)rt, rn: (byte)rn, rm: (byte)rm, shift: (byte)sh, size: 4);
        }

        // ── LDRB.W: F890 nnnn tttt iiiiiiiiiiii ──
        if ((hw0 & 0xFFF0) == 0xF890)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int imm12 = hw1 & 0xFFF;
            return new(raw, va, Thumb2Opcode.LDRB_IMM, rd: (byte)rt, rn: (byte)rn, immediate: imm12, size: 4);
        }

        // ── STRB.W: F880 nnnn tttt iiiiiiiiiiii ──
        if ((hw0 & 0xFFF0) == 0xF880)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int imm12 = hw1 & 0xFFF;
            return new(raw, va, Thumb2Opcode.STRB_IMM, rd: (byte)rt, rn: (byte)rn, immediate: imm12, size: 4);
        }

        // ── LDRH.W: F8B0 nnnn tttt iiiiiiiiiiii ──
        if ((hw0 & 0xFFF0) == 0xF8B0)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int imm12 = hw1 & 0xFFF;
            return new(raw, va, Thumb2Opcode.LDRH_IMM, rd: (byte)rt, rn: (byte)rn, immediate: imm12, size: 4);
        }

        // ── STRH.W: F8A0 nnnn tttt iiiiiiiiiiii ──
        if ((hw0 & 0xFFF0) == 0xF8A0)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int imm12 = hw1 & 0xFFF;
            return new(raw, va, Thumb2Opcode.STRH_IMM, rd: (byte)rt, rn: (byte)rn, immediate: imm12, size: 4);
        }

        // ── LDRSB.W: F910 nnnn tttt iiiiiiiiiiii ──
        if ((hw0 & 0xFFF0) == 0xF910)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int imm12 = hw1 & 0xFFF;
            return new(raw, va, Thumb2Opcode.LDRSB_IMM, rd: (byte)rt, rn: (byte)rn, immediate: imm12, size: 4);
        }

        // ── LDRSH.W: F930 nnnn tttt iiiiiiiiiiii ──
        if ((hw0 & 0xFFF0) == 0xF930)
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int imm12 = hw1 & 0xFFF;
            return new(raw, va, Thumb2Opcode.LDRSH_IMM, rd: (byte)rt, rn: (byte)rn, immediate: imm12, size: 4);
        }

        // ── LDRD: E9D0 nnnn tttt tt2i iiiiiiii ──
        if ((hw0 & 0xFFF0) == 0xE9D0 || ((hw0 & 0xFFF0) == 0xE950))
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int rt2 = (hw1 >> 8) & 0xF;
            int imm8 = (hw1 & 0xFF) * 4;
            bool up = (hw0 & 0x0080) != 0;
            int offset = up ? imm8 : -imm8;
            return new(raw, va, Thumb2Opcode.LDRD_IMM, rd: (byte)rt, rm: (byte)rt2, rn: (byte)rn, immediate: offset, size: 4);
        }

        // ── STRD: E9C0 nnnn tttt tt2i iiiiiiii ──
        if ((hw0 & 0xFFF0) == 0xE9C0 || ((hw0 & 0xFFF0) == 0xE940))
        {
            int rn = hw0 & 0xF;
            int rt = (hw1 >> 12) & 0xF;
            int rt2 = (hw1 >> 8) & 0xF;
            int imm8 = (hw1 & 0xFF) * 4;
            bool up = (hw0 & 0x0080) != 0;
            int offset = up ? imm8 : -imm8;
            return new(raw, va, Thumb2Opcode.STRD_IMM, rd: (byte)rt, rm: (byte)rt2, rn: (byte)rn, immediate: offset, size: 4);
        }

        // ── LDR.W Rt, [PC, #±imm12] (literal): F8DF tttt iiiiiiiiiiii or F85F tttt iiiiiiiiiiii ──
        if ((hw0 & 0xFF7F) == 0xF85F)
        {
            int rt = (hw1 >> 12) & 0xF;
            int imm12 = hw1 & 0xFFF;
            bool up = (hw0 & 0x0080) != 0;
            int offset = up ? imm12 : -imm12;
            ulong pc = (va + 4) & ~3UL;
            long target = (long)pc + offset;
            return new(raw, va, Thumb2Opcode.LDR_LIT, rd: (byte)rt, immediate: target, size: 4);
        }

        // ── ADD.W/SUB.W with immediate (modified constant): F1xx ──
        // ADD.W: F100-F10F or F110-F11F (S=1)
        if ((hw0 & 0xFBE0) == 0xF100)
        {
            int rn = hw0 & 0xF;
            int S = (hw0 >> 4) & 1;
            int rd = (hw1 >> 8) & 0xF;
            long imm = DecodeThumbExpandImm(hw0, hw1);
            if (rd == 15 && S != 0) // CMN alias
                return new(raw, va, Thumb2Opcode.CMP_IMM, rn: (byte)rn, immediate: imm, size: 4);
            return new(raw, va, Thumb2Opcode.ADD_IMM, rd: (byte)rd, rn: (byte)rn, immediate: imm, size: 4);
        }

        // SUB.W: F1A0-F1AF or F1B0-F1BF (S=1)
        if ((hw0 & 0xFBE0) == 0xF1A0)
        {
            int rn = hw0 & 0xF;
            int S = (hw0 >> 4) & 1;
            int rd = (hw1 >> 8) & 0xF;
            long imm = DecodeThumbExpandImm(hw0, hw1);
            if (rd == 15 && S != 0) // CMP alias
                return new(raw, va, Thumb2Opcode.CMP_IMM, rn: (byte)rn, immediate: imm, size: 4);
            return new(raw, va, Thumb2Opcode.SUB_IMM, rd: (byte)rd, rn: (byte)rn, immediate: imm, size: 4);
        }

        // ── ADD.W plain 12-bit: F200 nnnn 0iii dddd iiiiiiii ──
        if ((hw0 & 0xFBF0) == 0xF200 && (hw1 & 0x8000) == 0)
        {
            int rn = hw0 & 0xF;
            int rd = (hw1 >> 8) & 0xF;
            int i = (hw0 >> 10) & 1;
            int imm3 = (hw1 >> 12) & 0x7;
            int imm8 = hw1 & 0xFF;
            int imm12 = (i << 11) | (imm3 << 8) | imm8;
            return new(raw, va, Thumb2Opcode.ADD_IMM, rd: (byte)rd, rn: (byte)rn, immediate: imm12, size: 4);
        }

        // ── SUB.W plain 12-bit: F2A0 nnnn 0iii dddd iiiiiiii ──
        if ((hw0 & 0xFBF0) == 0xF2A0 && (hw1 & 0x8000) == 0)
        {
            int rn = hw0 & 0xF;
            int rd = (hw1 >> 8) & 0xF;
            int i = (hw0 >> 10) & 1;
            int imm3 = (hw1 >> 12) & 0x7;
            int imm8 = hw1 & 0xFF;
            int imm12 = (i << 11) | (imm3 << 8) | imm8;
            return new(raw, va, Thumb2Opcode.SUB_IMM, rd: (byte)rd, rn: (byte)rn, immediate: imm12, size: 4);
        }

        // ── ADD.W/SUB.W register: EB00/EBA0 ──
        if ((hw0 & 0xFFE0) == 0xEB00)
        {
            int rn = hw0 & 0xF;
            int rd = (hw1 >> 8) & 0xF;
            int rm = hw1 & 0xF;
            int shift = ((hw1 >> 6) & 0x3) << 0 | ((hw1 >> 12) & 0x7) << 2; // type:imm5
            return new(raw, va, Thumb2Opcode.ADD_REG, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, shift: (byte)(shift >> 2), size: 4);
        }
        if ((hw0 & 0xFFE0) == 0xEBA0)
        {
            int rn = hw0 & 0xF;
            int rd = (hw1 >> 8) & 0xF;
            int rm = hw1 & 0xF;
            return new(raw, va, Thumb2Opcode.SUB_REG, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, size: 4);
        }

        // ── CMP.W Rn, #const: F1B0 nnnn 0iii 1111 iiiiiiii ──
        if ((hw0 & 0xFBF0) == 0xF1B0 && ((hw1 >> 8) & 0xF) == 0xF)
        {
            int rn = hw0 & 0xF;
            long imm = DecodeThumbExpandImm(hw0, hw1);
            return new(raw, va, Thumb2Opcode.CMP_IMM, rn: (byte)rn, immediate: imm, size: 4);
        }

        // ── CMP.W Rn, Rm: EBB0 nnnn 0f00 mmmm ──
        if ((hw0 & 0xFFF0) == 0xEBB0 && ((hw1 >> 8) & 0xF) == 0xF)
        {
            int rn = hw0 & 0xF;
            int rm = hw1 & 0xF;
            return new(raw, va, Thumb2Opcode.CMP_REG, rn: (byte)rn, rm: (byte)rm, size: 4);
        }

        // ── AND.W / ORR.W / EOR.W / BIC.W / TST.W with modified immediate ──
        // AND.W: F000-F00F / F010-F01F (S=1)
        if ((hw0 & 0xFBE0) == 0xF000)
        {
            int rn = hw0 & 0xF;
            int S = (hw0 >> 4) & 1;
            int rd = (hw1 >> 8) & 0xF;
            long imm = DecodeThumbExpandImm(hw0, hw1);
            if (rd == 15 && S != 0) // TST alias
                return new(raw, va, Thumb2Opcode.TST_IMM, rn: (byte)rn, immediate: imm, size: 4);
            return new(raw, va, Thumb2Opcode.AND_IMM, rd: (byte)rd, rn: (byte)rn, immediate: imm, size: 4);
        }
        // ORR.W: F040-F04F
        if ((hw0 & 0xFBE0) == 0xF040)
        {
            int rn = hw0 & 0xF;
            int rd = (hw1 >> 8) & 0xF;
            long imm = DecodeThumbExpandImm(hw0, hw1);
            if (rn == 15) // MOV alias
                return new(raw, va, Thumb2Opcode.MOV_IMM, rd: (byte)rd, immediate: imm, size: 4);
            return new(raw, va, Thumb2Opcode.ORR_IMM, rd: (byte)rd, rn: (byte)rn, immediate: imm, size: 4);
        }
        // EOR.W: F080-F08F
        if ((hw0 & 0xFBE0) == 0xF080)
        {
            int rn = hw0 & 0xF;
            int rd = (hw1 >> 8) & 0xF;
            long imm = DecodeThumbExpandImm(hw0, hw1);
            return new(raw, va, Thumb2Opcode.EOR_IMM, rd: (byte)rd, rn: (byte)rn, immediate: imm, size: 4);
        }
        // BIC.W: F020-F02F
        if ((hw0 & 0xFBE0) == 0xF020)
        {
            int rn = hw0 & 0xF;
            int rd = (hw1 >> 8) & 0xF;
            long imm = DecodeThumbExpandImm(hw0, hw1);
            return new(raw, va, Thumb2Opcode.BIC_IMM, rd: (byte)rd, rn: (byte)rn, immediate: imm, size: 4);
        }
        // MVN.W: F060-F06F
        if ((hw0 & 0xFBE0) == 0xF060)
        {
            int rd = (hw1 >> 8) & 0xF;
            long imm = DecodeThumbExpandImm(hw0, hw1);
            return new(raw, va, Thumb2Opcode.MVN_IMM, rd: (byte)rd, immediate: imm, size: 4);
        }

        // ── AND/ORR/EOR/BIC register (32-bit): EA0x / EA4x / EA8x / EA2x ──
        if ((hw0 & 0xFFE0) == 0xEA00) { int rn = hw0 & 0xF; int rd = (hw1 >> 8) & 0xF; int rm = hw1 & 0xF; return new(raw, va, Thumb2Opcode.AND_REG, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, size: 4); }
        if ((hw0 & 0xFFE0) == 0xEA40) { int rn = hw0 & 0xF; int rd = (hw1 >> 8) & 0xF; int rm = hw1 & 0xF; return new(raw, va, Thumb2Opcode.ORR_REG, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, size: 4); }
        if ((hw0 & 0xFFE0) == 0xEA80) { int rn = hw0 & 0xF; int rd = (hw1 >> 8) & 0xF; int rm = hw1 & 0xF; return new(raw, va, Thumb2Opcode.EOR_REG, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, size: 4); }
        if ((hw0 & 0xFFE0) == 0xEA20) { int rn = hw0 & 0xF; int rd = (hw1 >> 8) & 0xF; int rm = hw1 & 0xF; return new(raw, va, Thumb2Opcode.BIC_REG, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, size: 4); }

        // ── MUL: FB00 nnnn 1111 mmmm ──
        if ((hw0 & 0xFFF0) == 0xFB00 && ((hw1 >> 12) & 0xF) == 0xF)
        {
            int rd = (hw1 >> 8) & 0xF;
            int rn = hw0 & 0xF;
            int rm = hw1 & 0xF;
            return new(raw, va, Thumb2Opcode.MUL, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, size: 4);
        }

        // ── MLA: FB00 nnnn aaaa mmmm (Ra != 0xF) ──
        if ((hw0 & 0xFFF0) == 0xFB00 && ((hw1 >> 12) & 0xF) != 0xF)
        {
            int rd = (hw1 >> 8) & 0xF;
            int rn = hw0 & 0xF;
            int rm = hw1 & 0xF;
            int ra = (hw1 >> 12) & 0xF;
            return new(raw, va, Thumb2Opcode.MLA, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, shift: (byte)ra, size: 4);
        }

        // ── SDIV: FB90 nnnn (f)dddd mmmm ──
        if ((hw0 & 0xFFF0) == 0xFB90)
        {
            int rd = (hw1 >> 8) & 0xF;
            int rn = hw0 & 0xF;
            int rm = hw1 & 0xF;
            return new(raw, va, Thumb2Opcode.SDIV, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, size: 4);
        }

        // ── UDIV: FBB0 nnnn (f)dddd mmmm ──
        if ((hw0 & 0xFFF0) == 0xFBB0)
        {
            int rd = (hw1 >> 8) & 0xF;
            int rn = hw0 & 0xF;
            int rm = hw1 & 0xF;
            return new(raw, va, Thumb2Opcode.UDIV, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, size: 4);
        }

        // ── Shift (32-bit): LSL.W FA00, LSR.W FA20, ASR.W FA40 ──
        if ((hw0 & 0xFFE0) == 0xFA00) { int rn = hw0 & 0xF; int rd = (hw1 >> 8) & 0xF; int rm = hw1 & 0xF; return new(raw, va, Thumb2Opcode.LSL_REG, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, size: 4); }
        if ((hw0 & 0xFFE0) == 0xFA20) { int rn = hw0 & 0xF; int rd = (hw1 >> 8) & 0xF; int rm = hw1 & 0xF; return new(raw, va, Thumb2Opcode.LSR_REG, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, size: 4); }
        if ((hw0 & 0xFFE0) == 0xFA40) { int rn = hw0 & 0xF; int rd = (hw1 >> 8) & 0xF; int rm = hw1 & 0xF; return new(raw, va, Thumb2Opcode.ASR_REG, rd: (byte)rd, rn: (byte)rn, rm: (byte)rm, size: 4); }

        // ── MOV.W Rd, Rm (register): EA4F 0d00 00sh mmmm ──
        if ((hw0 & 0xFFEF) == 0xEA4F && (hw1 & 0x0030) == 0x0000)
        {
            int rd = (hw1 >> 8) & 0xF;
            int rm = hw1 & 0xF;
            int imm5 = ((hw1 >> 12) & 0x7) << 2 | ((hw1 >> 6) & 0x3);
            if (imm5 == 0) // Pure MOV
                return new(raw, va, Thumb2Opcode.MOV_REG, rd: (byte)rd, rm: (byte)rm, size: 4);
            // LSL by immediate
            return new(raw, va, Thumb2Opcode.LSL_IMM, rd: (byte)rd, rn: (byte)rm, immediate: imm5, size: 4);
        }
        // LSR.W by immediate: EA4F + type=01
        if ((hw0 & 0xFFEF) == 0xEA4F && (hw1 & 0x0030) == 0x0010)
        {
            int rd = (hw1 >> 8) & 0xF;
            int rm = hw1 & 0xF;
            int imm5 = ((hw1 >> 12) & 0x7) << 2 | ((hw1 >> 6) & 0x3);
            if (imm5 == 0) imm5 = 32;
            return new(raw, va, Thumb2Opcode.LSR_IMM, rd: (byte)rd, rn: (byte)rm, immediate: imm5, size: 4);
        }
        // ASR.W by immediate: EA4F + type=10
        if ((hw0 & 0xFFEF) == 0xEA4F && (hw1 & 0x0030) == 0x0020)
        {
            int rd = (hw1 >> 8) & 0xF;
            int rm = hw1 & 0xF;
            int imm5 = ((hw1 >> 12) & 0x7) << 2 | ((hw1 >> 6) & 0x3);
            if (imm5 == 0) imm5 = 32;
            return new(raw, va, Thumb2Opcode.ASR_IMM, rd: (byte)rd, rn: (byte)rm, immediate: imm5, size: 4);
        }

        // ── DMB: F3BF 8F5x ──
        if (hw0 == 0xF3BF && (hw1 & 0xFFF0) == 0x8F50)
        {
            return new(raw, va, Thumb2Opcode.DMB, size: 4);
        }

        // ═══════════════════════════════════════════════════════════════
        // VFP / Floating-Point (Coprocessor 10/11)
        // ═══════════════════════════════════════════════════════════════

        // ── VLDR/VSTR: ED90/ED10/EDD0/ED50 (and ED1x/ED5x for negative offset) ──
        // VLDR.F32: ED9n nnnn dddd 0A ii_iiiiii
        // VLDR.F64: EDD0 nnnn dddd 0B ii_iiiiii
        if ((hw0 & 0xFF00) == 0xED00 || (hw0 & 0xFF00) == 0xEC00)
        {
            bool isLoad = (hw0 & 0x0010) != 0; // L bit
            bool up = (hw0 & 0x0080) != 0;      // U bit
            int rn = hw0 & 0xF;
            int imm8 = (hw1 & 0xFF) * 4;
            int offset = up ? imm8 : -imm8;
            bool isDouble = (hw1 & 0x0100) != 0; // D size: 0=single, 1=double
            int vd;
            if (isDouble)
            {
                vd = ((hw1 >> 12) & 0xF) | (((hw0 >> 6) & 1) << 4); // D:Vd (but only 16 D regs)
                vd = (hw1 >> 12) & 0xF;
            }
            else
            {
                vd = ((hw1 >> 12) & 0xF) << 1 | ((hw1 >> 22) & 1); // Vd:D
                vd = ((hw1 >> 12) & 0xF); // simplified
            }
            byte width = (byte)(isDouble ? 64 : 32);
            var op = isLoad ? Thumb2Opcode.VLDR : Thumb2Opcode.VSTR;
            return new(raw, va, op, rd: (byte)vd, rn: (byte)rn, immediate: offset, shift: width, size: 4);
        }

        // ── VMOV (GP ↔ FP): EE00/EE10 ──
        // VMOV Sn, Rt: EE0n 0ttt 1010 0000 (GP→FP)
        // VMOV Rt, Sn: EE1n 0ttt 1010 0000 (FP→GP)
        if ((hw0 & 0xFFE0) == 0xEE00 && (hw1 & 0x0F10) == 0x0A10)
        {
            int rt = (hw1 >> 12) & 0xF;
            int sn = ((hw0 & 0xF) << 1) | ((hw1 >> 7) & 1);
            bool toFP = (hw0 & 0x0010) == 0; // op=0: GP→FP, op=1: FP→GP
            if (toFP)
                return new(raw, va, Thumb2Opcode.VMOV_GP_TO_FP, rd: (byte)sn, rn: (byte)rt, size: 4);
            else
                return new(raw, va, Thumb2Opcode.VMOV_FP_TO_GP, rd: (byte)rt, rn: (byte)sn, size: 4);
        }

        // ── VADD/VSUB/VMUL/VDIV/VNEG/VABS/VSQRT/VCMP (F32/F64) ──
        // These all have pattern EE?? ???? ?A?? ????
        if ((hw0 & 0xEF00) == 0xEE00 && (hw1 & 0x0E00) == 0x0A00)
        {
            return DecodeVfpDataProcessing(raw, hw0, hw1, va);
        }

        // ── VMRS APSR_nzcv, FPSCR: EEF1 FA10 ──
        if (hw0 == 0xEEF1 && hw1 == 0xFA10)
        {
            return new(raw, va, Thumb2Opcode.VMRS, size: 4);
        }

        // ── VCVT variants ──
        // Handled inside VFP data processing above

        return new(raw, va, Thumb2Opcode.Unknown, size: 4);
    }

    // ════════════════════════════════════════════════════════════════════
    // VFP data-processing decoder
    // ════════════════════════════════════════════════════════════════════

    private static Thumb2Instruction DecodeVfpDataProcessing(uint raw, ushort hw0, ushort hw1, ulong va)
    {
        bool isDouble = (hw1 & 0x0100) != 0;
        byte width = (byte)(isDouble ? 64 : 32);

        int opc1 = (hw0 >> 4) & 0xF;
        int opc2 = (hw1 >> 6) & 0x3;
        int opc3 = (hw1 >> 4) & 0x3; // bits [5:4] of hw1

        // Extract Vd, Vn, Vm for single and double
        int vd, vn, vm;
        if (isDouble)
        {
            vd = (hw1 >> 12) & 0xF;
            vn = (hw0 & 0xF);
            vm = hw1 & 0xF;
        }
        else
        {
            vd = ((hw1 >> 12) & 0xF);
            vn = (hw0 & 0xF);
            vm = (hw1 & 0xF);
        }

        // VADD: opc1=0b0011, opc3[0]=0
        if ((opc1 & 0xB) == 0x3 && (opc3 & 1) == 0)
            return new(raw, va, Thumb2Opcode.VADD, rd: (byte)vd, rn: (byte)vn, rm: (byte)vm, shift: width, size: 4);

        // VSUB: opc1=0b0011, opc3[0]=1
        if ((opc1 & 0xB) == 0x3 && (opc3 & 1) == 1)
            return new(raw, va, Thumb2Opcode.VSUB, rd: (byte)vd, rn: (byte)vn, rm: (byte)vm, shift: width, size: 4);

        // VMUL: opc1=0b0010, opc3[0]=0
        if ((opc1 & 0xB) == 0x2 && (opc3 & 1) == 0)
            return new(raw, va, Thumb2Opcode.VMUL, rd: (byte)vd, rn: (byte)vn, rm: (byte)vm, shift: width, size: 4);

        // VDIV: opc1=0b1000
        if ((opc1 & 0xB) == 0x8)
            return new(raw, va, Thumb2Opcode.VDIV, rd: (byte)vd, rn: (byte)vn, rm: (byte)vm, shift: width, size: 4);

        // VNEG: opc1=0b1011, opc2=01, opc3[0]=1
        if ((opc1 & 0xB) == 0xB && opc2 == 0x01 && (opc3 & 1) == 1)
            return new(raw, va, Thumb2Opcode.VNEG, rd: (byte)vd, rm: (byte)vm, shift: width, size: 4);

        // VABS: opc1=0b1011, opc2=00, opc3[0]=1
        if ((opc1 & 0xB) == 0xB && opc2 == 0x00 && (opc3 & 1) == 1)
            return new(raw, va, Thumb2Opcode.VABS, rd: (byte)vd, rm: (byte)vm, shift: width, size: 4);

        // VMOV (register): opc1=0b1011, opc2=00, opc3[0]=0
        if ((opc1 & 0xB) == 0xB && opc2 == 0x00 && (opc3 & 1) == 0)
            return new(raw, va, Thumb2Opcode.VMOV_REG, rd: (byte)vd, rm: (byte)vm, shift: width, size: 4);

        // VSQRT: opc1=0b1011, opc2=01, opc3[0]=0 — wait, VSQRT is opc2=11
        if ((opc1 & 0xB) == 0xB && opc2 == 0x03 && (opc3 & 1) == 1)
            return new(raw, va, Thumb2Opcode.VSQRT, rd: (byte)vd, rm: (byte)vm, shift: width, size: 4);

        // VCMP: opc1=0b1011, opc2=01 (or 11), opc3=01
        if ((opc1 & 0xB) == 0xB && (opc2 & 0x2) != 0 && (opc3 & 1) == 0)
            return new(raw, va, Thumb2Opcode.VCMP, rn: (byte)vd, rm: (byte)vm, shift: width, size: 4);

        // VCVT variants: opc1=0b1011, opc2=10
        if ((opc1 & 0xB) == 0xB && opc2 == 0x02)
        {
            // Various VCVT forms based on opc3
            if (!isDouble && (opc3 & 1) == 1) // VCVT.F32.S32 or VCVT.F32.U32
                return new(raw, va, Thumb2Opcode.VCVT_F32_S32, rd: (byte)vd, rm: (byte)vm, size: 4);
            if (!isDouble && (opc3 & 1) == 0) // VCVT.S32.F32 or VCVT.U32.F32
                return new(raw, va, Thumb2Opcode.VCVT_S32_F32, rd: (byte)vd, rm: (byte)vm, size: 4);
        }

        // VCVT F64↔F32: opc1=0b1011, opc2=11, opc3[0]=1
        if ((opc1 & 0xB) == 0xB && opc2 == 0x03 && (opc3 & 1) == 0)
        {
            if (isDouble)
                return new(raw, va, Thumb2Opcode.VCVT_F64_F32, rd: (byte)vd, rm: (byte)vm, size: 4);
            else
                return new(raw, va, Thumb2Opcode.VCVT_F32_F64, rd: (byte)vd, rm: (byte)vm, size: 4);
        }

        return new(raw, va, Thumb2Opcode.Unknown, size: 4);
    }

    // ════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Decode BL/BLX target address from hw0/hw1.
    /// Source: ARMv7-M §A8.8.25.
    /// </summary>
    private static long DecodeBLTarget(ushort hw0, ushort hw1, ulong va)
    {
        int S = (hw0 >> 10) & 1;
        int imm10 = hw0 & 0x3FF;
        int J1 = (hw1 >> 13) & 1;
        int J2 = (hw1 >> 11) & 1;
        int imm11 = hw1 & 0x7FF;

        int I1 = (~(J1 ^ S)) & 1;
        int I2 = (~(J2 ^ S)) & 1;

        int rawOffset = (S << 24) | (I1 << 23) | (I2 << 22) | (imm10 << 12) | (imm11 << 1);
        if ((rawOffset & (1 << 24)) != 0)
            rawOffset |= unchecked((int)0xFE000000);

        return (long)(va + 4) + rawOffset;
    }

    /// <summary>
    /// Decode Thumb2 modified immediate constant (ThumbExpandImm).
    /// Source: ARMv7-M §A6.3.2.
    ///
    /// The 12-bit constant is encoded as i:imm3:imm8 across hw0 and hw1:
    ///   i    = hw0[10]
    ///   imm3 = hw1[14:12]
    ///   imm8 = hw1[7:0]
    /// </summary>
    private static long DecodeThumbExpandImm(ushort hw0, ushort hw1)
    {
        int i = (hw0 >> 10) & 1;
        int imm3 = (hw1 >> 12) & 0x7;
        int imm8 = hw1 & 0xFF;
        int imm12 = (i << 11) | (imm3 << 8) | imm8;

        if ((imm12 & 0xC00) == 0)
        {
            // Unmodified value
            return (imm12 >> 8) switch
            {
                0b00 => imm8,
                0b01 => (imm8 << 16) | imm8,
                0b10 => (imm8 << 24) | (imm8 << 8),
                0b11 => (imm8 << 24) | (imm8 << 16) | (imm8 << 8) | imm8,
                _ => imm8
            };
        }

        // Rotated byte
        int unrotated = 0x80 | (imm12 & 0x7F);
        int rotation = (imm12 >> 7) & 0x1F;
        uint result = (uint)((unrotated >> rotation) | (unrotated << (32 - rotation)));
        return (long)result;
    }
}
