namespace Rosetta.Binary;

/// <summary>
/// Decoded ARM64 (AArch64) instruction.
///
/// Source evidence:
///   ARM Architecture Reference Manual ARMv8-A (DDI 0487)
///   Encoding groups — §C4 A64 instruction set encoding
///
/// Only the instruction families emitted by Clang for IL2CPP codegen are decoded:
///   - MOVZ/MOVK/MOVN (immediate load)
///   - ADRP (PC-relative page address)
///   - ADD immediate (offset into page)
///   - LDR/STR (register + immediate offset, pre/post-index, literal)
///   - LDP/STP (pair load/store)
///   - BL/B/BR/BLR/RET (branches)
///   - NOP
///   - STP/LDP for prologue/epilogue (stack frame)
///   - FMOV (floating point move)
///   - CBZ/CBNZ/TBZ/TBNZ (conditional branches)
/// </summary>
public readonly struct Arm64Instruction
{
    /// <summary>Raw 32-bit instruction word (little-endian).</summary>
    public readonly uint RawValue;

    /// <summary>Virtual address of this instruction.</summary>
    public readonly ulong Address;

    /// <summary>Decoded opcode family.</summary>
    public readonly Arm64Opcode Opcode;

    /// <summary>Destination/source register (Rd or Rt), 0-30 = X/W regs, 31 = SP or ZR depending on context.</summary>
    public readonly byte Rd;

    /// <summary>First source register (Rn), e.g. base register for LDR/STR.</summary>
    public readonly byte Rn;

    /// <summary>Second source register (Rm).</summary>
    public readonly byte Rm;

    /// <summary>Immediate value (sign-extended where appropriate).</summary>
    public readonly long Immediate;

    /// <summary>Whether the instruction operates on 64-bit registers (X) vs 32-bit (W).</summary>
    public readonly bool Is64Bit;

    /// <summary>Whether the offset register (Rm) is 64-bit (X) vs 32-bit (W). Only valid for register offset loads/stores.</summary>
    public readonly bool OffsetIs64Bit;

    /// <summary>Shift amount for MOVK (0, 16, 32, 48).</summary>
    public readonly byte Shift;

    /// <summary>Condition code for conditional instructions.</summary>
    public readonly byte Condition;

    /// <summary>Writeback mode: 0=none, 1=post-index ([Rn],#imm), 2=pre-index ([Rn,#imm]!).</summary>
    public readonly byte Writeback;

    /// <summary>Extension option (bits 15:13) for register offsets.</summary>
    public readonly byte ExtendOption;

    /// <summary>Whether the register offset is scaled (S bit).</summary>
    public readonly bool IsScaled;

    /// <summary>Type of shift applied to register operands (0=LSL, 1=LSR, 2=ASR, 3=ROR).</summary>
    public readonly byte ShiftType;

    /// <summary>Whether the register operand is extended (e.g. SXTW) rather than shifted.</summary>
    public readonly bool IsExtended;

    /// <summary>Whether the load/store operation has an unscaled offset.</summary>
    public readonly bool IsUnscaled;

    public Arm64Instruction(uint raw, ulong address, Arm64Opcode opcode,
        byte rd = 0, byte rn = 0, byte rm = 0, long immediate = 0,
        bool is64Bit = false, byte shift = 0, byte condition = 0,
        byte writeback = 0, bool offsetIs64Bit = false,
        byte extendOption = 0, bool isScaled = false,
        byte shiftType = 0, bool isExtended = false,
        bool isUnscaled = false)
    {
        RawValue = raw;
        Address = address;
        Opcode = opcode;
        Rd = rd;
        Rn = rn;
        Rm = rm;
        Immediate = immediate;
        Is64Bit = is64Bit;
        Shift = shift;
        Condition = condition;
        Writeback = writeback;
        OffsetIs64Bit = offsetIs64Bit;
        ExtendOption = extendOption;
        IsScaled = isScaled;
        ShiftType = shiftType;
        IsExtended = isExtended;
        IsUnscaled = isUnscaled;
    }

    /// <summary>
    /// Format a GP register as SP when reg=31 and context is a base/stack register
    /// (LDR/STR/LDP/STP/ADD/SUB base), or as XZR/WZR when reg=31 in data-processing context.
    /// Source: ARM ARM §C1.2.5 — Register 31 is context-dependent.
    /// </summary>
    private static string GpReg(byte reg, bool is64, bool isSp)
    {
        if (reg == 31)
            return isSp ? "SP" : (is64 ? "XZR" : "WZR");
        return (is64 ? "X" : "W") + reg;
    }

    /// <summary>Format a signed immediate as decimal for small values, hex for large.</summary>
    private static int VectorSrcElementIndex(byte imm4, byte imm5)
    {
        if ((imm5 & 1) == 1) return imm4;
        if ((imm5 & 2) == 2) return imm4 >> 1;
        if ((imm5 & 4) == 4) return imm4 >> 2;
        if ((imm5 & 8) == 8) return imm4 >> 3;
        return 0;
    }

    private static string VectorArrangement(byte imm5, bool q)
    {
        if ((imm5 & 1) == 1) return q ? "16B" : "8B";
        if ((imm5 & 2) == 2) return q ? "8H" : "4H";
        if ((imm5 & 4) == 4) return q ? "4S" : "2S";
        if ((imm5 & 8) == 8) return "2D";
        return "?";
    }

    private static string VectorElementSize(byte imm5)
    {
        if ((imm5 & 1) == 1) return "B";
        if ((imm5 & 2) == 2) return "H";
        if ((imm5 & 4) == 4) return "S";
        if ((imm5 & 8) == 8) return "D";
        return "?";
    }

    private static int VectorElementIndex(byte imm5)
    {
        if ((imm5 & 1) == 1) return imm5 >> 1;
        if ((imm5 & 2) == 2) return imm5 >> 2;
        if ((imm5 & 4) == 4) return imm5 >> 3;
        if ((imm5 & 8) == 8) return imm5 >> 4;
        return 0;
    }

    private static string FormatSimdMovi(byte rd, long imm, byte cmode, bool q, uint raw)
    {
        int op = (int)((raw >> 29) & 1);
        if (cmode == 0b1111)
        {
            string farr = "";
            if (op == 0) farr = q ? "4S" : "2S";
            else farr = q ? "2D" : "1D"; // op == 1
            
            // In C# "F" format might produce 10.0, Capstone might produce 10.00000000
            // But CapstoneIrComparator removes trailing zeros, so 10.0 or 1.0 is fine!
            return $"FMOV V{rd}.{farr}, #{DecodeFpImm8((byte)imm, op == 1)}";
        }

        string arr;
        string shiftStr = "";
        if ((cmode & 0b1110) == 0b0000) { arr = q ? "4S" : "2S"; shiftStr = ""; } // 32-bit, LSL 0
        else if ((cmode & 0b1110) == 0b0010) { arr = q ? "4S" : "2S"; shiftStr = ", LSL #8"; }
        else if ((cmode & 0b1110) == 0b0100) { arr = q ? "4S" : "2S"; shiftStr = ", LSL #16"; }
        else if ((cmode & 0b1110) == 0b0110) { arr = q ? "4S" : "2S"; shiftStr = ", LSL #24"; }
        else if ((cmode & 0b1111) == 0b1000) { arr = q ? "8H" : "4H"; shiftStr = ""; }
        else if ((cmode & 0b1111) == 0b1010) { arr = q ? "8H" : "4H"; shiftStr = ", LSL #8"; }
        else if ((cmode & 0b1111) == 0b1110)
        {
            if (op == 0)
                return $"MOVI V{rd}.{(q ? "16B" : "8B")}, #0x{imm:X}";
            else
            {
                ulong imm64 = 0;
                for (int i = 0; i < 8; i++)
                    if ((imm & (1L << i)) != 0)
                        imm64 |= (0xFFUL << (i * 8));
                if (q) return $"MOVI V{rd}.2D, #0x{imm64:X}";
                else return $"MOVI D{rd}, #0x{imm64:X}";
            }
        }
        else return $"{(op == 1 ? "MVNI" : "MOVI")} V{rd}, #0x{imm:X}"; // Fallback

        return $"{(op == 1 ? "MVNI" : "MOVI")} V{rd}.{arr}, #0x{imm:X}{shiftStr}";
    }

    private static string FmtImm(long imm)
    {
        if (imm < 0) return $"-0x{-imm:X}";
        if (imm > 9) return $"#0x{imm:X}";
        return $"#{imm}";
    }

    private static string FmtAddSubImm(long imm)
    {
        if (imm > 0xFFF && (imm & 0xFFF) == 0)
            return $"#0x{imm >> 12:X}, LSL #12";
        return FmtImm(imm);
    }

    /// <summary>Format a register offset with optional extension and scaling.
    /// Source: ARM ARM §C1.3.3</summary>
    private string FmtRegOffset(byte rmReg, byte extend, bool isScaled, int scaleAmount)
    {
        string reg = GpReg(rmReg, (extend & 1) == 1, false);
        
        if (!isScaled || scaleAmount == 0)
        {
            if (extend == 0b011) return reg;
            if (extend == 0b010) return $"{reg}, UXTW";
            if (extend == 0b110) return $"{reg}, SXTW";
            if (extend == 0b111) return $"{reg}, SXTX";
            return reg;
        }

        if (extend == 0b011) return $"{reg}, LSL #{scaleAmount}";
        if (extend == 0b010) return $"{reg}, UXTW #{scaleAmount}";
        if (extend == 0b110) return $"{reg}, SXTW #{scaleAmount}";
        if (extend == 0b111) return $"{reg}, SXTX #{scaleAmount}";
        
        return reg;
    }

    /// <summary>Format a data processing register operand with optional shift or extension.</summary>
    private string FmtDpReg(byte rmReg, bool isExtended, byte shiftType, byte extendOption, byte shiftAmount, bool is64Bit)
    {
        string rmStr;

        if (isExtended)
        {
            // For extended registers, Rm is 64-bit only for UXTX (011) and SXTX (111).
            bool rmIs64 = (extendOption == 0b011 || extendOption == 0b111);
            rmStr = GpReg(rmReg, rmIs64, false);

            string extName = extendOption switch
            {
                0b000 => "UXTB", 0b001 => "UXTH", 0b010 => "UXTW", 0b011 => "UXTX",
                0b100 => "SXTB", 0b101 => "SXTH", 0b110 => "SXTW", 0b111 => "SXTX",
                _ => "UXTX"
            };

            if (shiftAmount == 0)
            {
                if (is64Bit && extendOption == 0b011) return rmStr;
                if (!is64Bit && extendOption == 0b010) return rmStr;
                return $"{rmStr}, {extName}";
            }
            return $"{rmStr}, {extName} #{shiftAmount}";
        }
        else
        {
            // Shifted register
            rmStr = GpReg(rmReg, is64Bit, false);
            
            if (shiftAmount == 0) return rmStr; // LSL #0 is omitted

            string shiftName = shiftType switch
            {
                0b00 => "LSL", 0b01 => "LSR", 0b10 => "ASR", 0b11 => "ROR",
                _ => "LSL"
            };

            return $"{rmStr}, {shiftName} #{shiftAmount}";
        }
    }

    /// <summary>Format a memory operand with proper pre/post-index writeback.
    /// wb=0: [Rn, #imm]   wb=1: [Rn], #imm (post)   wb=2: [Rn, #imm]! (pre)
    /// Source: ARM ARM §C1.3.3 — Addressing modes</summary>
    private string FmtMem(byte baseReg, long imm, byte wb)
    {
        string b = GpReg(baseReg, true, true); // base is always SP-context
        if (wb == 1) // post-index: offset after bracket
        {
            if (imm == 0) return $"[{b}]";
            return $"[{b}], {FmtImm(imm)}";
        }
        if (wb == 2) // pre-index: offset inside bracket with !
        {
            if (imm == 0) return $"[{b}]!";
            return $"[{b}, {FmtImm(imm)}]!";
        }
        // no writeback
        if (imm == 0) return $"[{b}]";
        return $"[{b}, {FmtImm(imm)}]";
    }

    public override string ToString()
    {
        // Shorthand for GP registers in data-processing context (reg31 = XZR/WZR)
        string rd = GpReg(Rd, Is64Bit, false);
        string rn = GpReg(Rn, Is64Bit, false);
        string rm = GpReg(Rm, Is64Bit, false);
        // SP-context for base registers in ADD/SUB (reg31 = SP)
        string rdSp = GpReg(Rd, Is64Bit, true);
        string rnSp = GpReg(Rn, Is64Bit, true);

        return Opcode switch
        {
            Arm64Opcode.MOVZ => $"MOVZ {rd}, #0x{(ushort)Immediate:X}, LSL #{Shift}",
            Arm64Opcode.MOVK => $"MOVK {rd}, #0x{(ushort)Immediate:X}, LSL #{Shift}",
            Arm64Opcode.MOVN => $"MOVN {rd}, #0x{(ushort)Immediate:X}, LSL #{Shift}",
            Arm64Opcode.ADRP => $"ADRP X{Rd}, #0x{(ulong)Immediate:X}",
            Arm64Opcode.ADR => $"ADR X{Rd}, #0x{(ulong)Immediate:X}",
            // ADD/SUB immediate — Rd and Rn use SP-context (ARM ARM §C6.2.4)
            Arm64Opcode.ADD_IMM => $"ADD {rdSp}, {rnSp}, {FmtAddSubImm(Immediate)}",
            Arm64Opcode.SUB_IMM => $"SUB {rdSp}, {rnSp}, {FmtAddSubImm(Immediate)}",
            // Load/Store unsigned immediate — base Rn is SP-context
            Arm64Opcode.LDR_IMM => $"{(IsUnscaled ? "LDUR" : "LDR")} {rd}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.STR_IMM => $"{(IsUnscaled ? "STUR" : "STR")} {rd}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.LDRB_IMM => $"{(IsUnscaled ? "LDURB" : "LDRB")} {GpReg(Rd, false, false)}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.STRB_IMM => $"{(IsUnscaled ? "STURB" : "STRB")} {GpReg(Rd, false, false)}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.LDRH_IMM => $"{(IsUnscaled ? "LDURH" : "LDRH")} {GpReg(Rd, false, false)}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.STRH_IMM => $"{(IsUnscaled ? "STURH" : "STRH")} {GpReg(Rd, false, false)}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.LDRSW_IMM => $"{(IsUnscaled ? "LDURSW" : "LDRSW")} X{Rd}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.LDR_LIT => $"LDR {GpReg(Rd, Is64Bit, false)}, 0x{(ulong)Immediate:X}",
            Arm64Opcode.LDR_LIT_FP => $"LDR {SimdRegName(Shift)}{Rd}, 0x{(ulong)Immediate:X}",
            // Load/Store pair — base Rn is SP-context, registers are ZR-context
            Arm64Opcode.LDP => $"LDP {rd}, {rm}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.LDPSW => $"LDPSW {GpReg(Rd, true, false)}, {GpReg(Rm, true, false)}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.STP => $"STP {rd}, {rm}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.BL => $"BL 0x{(ulong)Immediate:X}",
            Arm64Opcode.B => $"B 0x{(ulong)Immediate:X}",
            Arm64Opcode.BR => $"BR X{Rn}",
            Arm64Opcode.BLR => $"BLR X{Rn}",
            Arm64Opcode.RET => "RET",
            Arm64Opcode.CBZ => $"CBZ {rd}, #0x{(ulong)Immediate:X}",
            Arm64Opcode.CBNZ => $"CBNZ {rd}, #0x{(ulong)Immediate:X}",
            Arm64Opcode.TBZ => $"TBZ {GpReg(Rd, Is64Bit, false)}, #{Shift}, #0x{(ulong)Immediate:X}",
            Arm64Opcode.TBNZ => $"TBNZ {GpReg(Rd, Is64Bit, false)}, #{Shift}, #0x{(ulong)Immediate:X}",
            Arm64Opcode.NOP => "NOP",
            Arm64Opcode.FMOV_IMM => $"FMOV {(Is64Bit ? "D" : "S")}{Rd}, #imm",
            Arm64Opcode.B_COND => $"B.{CondName(Condition)} #0x{(ulong)Immediate:X}",
            Arm64Opcode.MOV_REG => $"MOV {rd}, {GpReg(Rn, Is64Bit, false)}",
            Arm64Opcode.MRS => $"MRS X{Rd}, S{((Immediate >> 14) & 3) + 2}_{(Immediate >> 11) & 7}_C{(Immediate >> 7) & 0xF}_C{(Immediate >> 3) & 0xF}_{Immediate & 7}",
            // SIMD/FP Load/Store
            Arm64Opcode.LDR_SIMD_IMM => $"{(IsUnscaled ? "LDUR" : "LDR")} {SimdRegName(Shift)}{Rd}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.STR_SIMD_IMM => $"{(IsUnscaled ? "STUR" : "STR")} {SimdRegName(Shift)}{Rd}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.LDR_SIMD_REG => $"LDR {SimdRegName(Shift)}{Rd}, [{GpReg(Rn, true, true)}, {FmtRegOffset(Rm, ExtendOption, IsScaled, Shift)}]",
            Arm64Opcode.STR_SIMD_REG => $"STR {SimdRegName(Shift)}{Rd}, [{GpReg(Rn, true, true)}, {FmtRegOffset(Rm, ExtendOption, IsScaled, Shift)}]",
            Arm64Opcode.LDP_SIMD => $"LDP {SimdRegName(Shift)}{Rd}, {SimdRegName(Shift)}{Rm}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.STP_SIMD => $"STP {SimdRegName(Shift)}{Rd}, {SimdRegName(Shift)}{Rm}, {FmtMem(Rn, Immediate, Writeback)}",
            // CMP — uses ZR-context for Rd (which is XZR by definition)
            Arm64Opcode.CMP_REG => $"CMP {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            Arm64Opcode.CMP_IMM => $"CMP {rn}, {FmtAddSubImm(Immediate)}",
            Arm64Opcode.SUBS_REG => (Rd == 31) 
                                    ? $"CMP {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}"
                                    : (Rn == 31)
                                        ? $"NEGS {rd}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}"
                                        : $"SUBS {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            Arm64Opcode.SUBS_IMM => $"SUBS {rd}, {rn}, {FmtAddSubImm(Immediate)}",
            // ADDS — CMN alias when Rd=XZR (ARM ARM §C6.2.43)
            Arm64Opcode.ADDS_IMM => Rd == 31
                ? $"CMN {rnSp}, {FmtAddSubImm(Immediate)}"
                : $"ADDS {rd}, {rnSp}, {FmtAddSubImm(Immediate)}",
            // Register arithmetic
            Arm64Opcode.ADD_REG => $"ADD {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            Arm64Opcode.SUB_REG => (Rn == 31)
                                    ? $"NEG {rd}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}"
                                    : $"SUB {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            Arm64Opcode.ADDS_REG => (Rd == 31) 
                                    ? $"CMN {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}"
                                    : $"ADDS {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            // Logical immediate
            Arm64Opcode.AND_IMM => $"AND {rdSp}, {rn}, #0x{Immediate:X}",
            Arm64Opcode.ORR_IMM => $"ORR {rdSp}, {rn}, #0x{Immediate:X}",
            Arm64Opcode.EOR_IMM => $"EOR {rdSp}, {rn}, #0x{Immediate:X}",
            // ANDS — TST alias when Rd=XZR/WZR (ARM ARM §C6.2.296)
            Arm64Opcode.ANDS_IMM => Rd == 31
                ? $"TST {rn}, #0x{Immediate:X}"
                : $"ANDS {rd}, {rn}, #0x{Immediate:X}",
            // Logical register
            Arm64Opcode.AND_REG => $"AND {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            Arm64Opcode.BIC_REG => $"BIC {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            Arm64Opcode.ORR_REG => $"ORR {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            Arm64Opcode.ORN_REG => (Rn == 31)
                                    ? $"MVN {rd}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}"
                                    : $"ORN {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            Arm64Opcode.EOR_REG => $"EOR {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            Arm64Opcode.EON_REG => $"EON {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            Arm64Opcode.ANDS_REG => Rd == 31 ? $"TST {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}" : $"ANDS {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            Arm64Opcode.BICS_REG => $"BICS {rd}, {rn}, {FmtDpReg(Rm, IsExtended, ShiftType, ExtendOption, Shift, Is64Bit)}",
            // Bitfield
            Arm64Opcode.UBFM => FormatBitfieldMove("UBFM", Rd, Rn, Rm, Shift, Is64Bit),
            Arm64Opcode.SBFM => FormatBitfieldMove("SBFM", Rd, Rn, Rm, Shift, Is64Bit),
            Arm64Opcode.BFM => FormatBitfieldMove("BFM", Rd, Rn, Rm, Shift, Is64Bit),
            Arm64Opcode.EXTR => Rn == Rm ? $"ROR {rd}, {rn}, #{Shift}" : $"EXTR {rd}, {rn}, {rm}, #{Shift}",
            // Conditional select
            Arm64Opcode.CSEL => $"CSEL {rd}, {rn}, {rm}, {CondName(Condition)}",
            Arm64Opcode.CSINC => (Rn == 31 && Rm == 31)
                ? $"CSET {rd}, {CondName((byte)(Condition ^ 1))}"
                : (Rn == Rm && Rn != 31)
                    ? $"CINC {rd}, {rn}, {CondName((byte)(Condition ^ 1))}"
                    : $"CSINC {rd}, {rn}, {rm}, {CondName(Condition)}",
            Arm64Opcode.CSINV => (Rn == 31 && Rm == 31)
                ? $"CSETM {rd}, {CondName((byte)(Condition ^ 1))}"
                : (Rn == Rm && Rn != 31)
                    ? $"CINV {rd}, {rn}, {CondName((byte)(Condition ^ 1))}"
                    : $"CSINV {rd}, {rn}, {rm}, {CondName(Condition)}",
            Arm64Opcode.CSNEG => (Rn == Rm && Rn != 31)
                ? $"CNEG {rd}, {rn}, {CondName((byte)(Condition ^ 1))}"
                : $"CSNEG {rd}, {rn}, {rm}, {CondName(Condition)}",
            // Multiply / Divide
            // MADD — MUL alias when Ra=XZR (ARM ARM §C6.2.190)
            Arm64Opcode.MADD => Shift == 31
                ? $"MUL {rd}, {rn}, {rm}"
                : $"MADD {rd}, {rn}, {rm}, {GpReg(Shift, Is64Bit, false)}",
            // MSUB — MNEG alias when Ra=XZR (ARM ARM §C6.2.189)
            Arm64Opcode.MSUB => Shift == 31
                ? $"MNEG {rd}, {rn}, {rm}"
                : $"MSUB {rd}, {rn}, {rm}, {GpReg(Shift, Is64Bit, false)}",
            Arm64Opcode.SDIV => $"SDIV {rd}, {rn}, {rm}",
            Arm64Opcode.UDIV => $"UDIV {rd}, {rn}, {rm}",
            // STR/LDR register offset
            Arm64Opcode.STR_REG => FormatLoadStoreRegOffset(false),
            Arm64Opcode.LDR_REG => FormatLoadStoreRegOffset(true),
            // FP data-processing 2-source — §C7.2.67-150
            Arm64Opcode.FADD => $"FADD {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}",
            Arm64Opcode.FSUB => $"FSUB {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}",
            Arm64Opcode.FMUL => $"FMUL {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}",
            Arm64Opcode.FDIV => $"FDIV {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}",
            Arm64Opcode.FNMUL => $"FNMUL {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}",
            // FP data-processing 3-source — §C7.2.100-103 (Ra stored in Shift field)
            Arm64Opcode.FMADD => $"FMADD {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}, {FpPrecName()}{Shift}",
            Arm64Opcode.FMSUB => $"FMSUB {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}, {FpPrecName()}{Shift}",
            Arm64Opcode.FNMADD => $"FNMADD {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}, {FpPrecName()}{Shift}",
            Arm64Opcode.FNMSUB => $"FNMSUB {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}, {FpPrecName()}{Shift}",
            // FP compare — §C7.2.72
            Arm64Opcode.FCMP => Rm == 0xFF
                ? $"FCMP {FpPrecName()}{Rn}, #0.0"
                : $"FCMP {FpPrecName()}{Rn}, {FpPrecName()}{Rm}",
            Arm64Opcode.FCCMP => $"FCCMP {FpPrecName()}{Rn}, {FpPrecName()}{Rm}, #{Immediate & 0xF}, {CondName(Condition)}",
            // FP data-processing 1-source — §C7.2.67-69
            Arm64Opcode.FABS => $"FABS {FpPrecName()}{Rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FNEG => $"FNEG {FpPrecName()}{Rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FSQRT => $"FSQRT {FpPrecName()}{Rd}, {FpPrecName()}{Rn}",
            // FP conversion between precisions — §C7.2.71
            Arm64Opcode.FCVT_PREC => Is64Bit
                ? $"FCVT D{Rd}, S{Rn}"  // single → double
                : $"FCVT S{Rd}, D{Rn}",  // double → single
            // FP ↔ integer conversion — §C7.2.73-80
            Arm64Opcode.FCVTZS => Immediate > 0 ? $"FCVTZS {rd}, {FpPrecName()}{Rn}, #{Immediate}" : $"FCVTZS {rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FCVTZU => Immediate > 0 ? $"FCVTZU {rd}, {FpPrecName()}{Rn}, #{Immediate}" : $"FCVTZU {rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.SCVTF => Immediate > 0 ? $"SCVTF {FpPrecName()}{Rd}, {rn}, #{Immediate}" : $"SCVTF {FpPrecName()}{Rd}, {rn}",
            Arm64Opcode.UCVTF => Immediate > 0 ? $"UCVTF {FpPrecName()}{Rd}, {rn}, #{Immediate}" : $"UCVTF {FpPrecName()}{Rd}, {rn}",
            // FP ↔ GP move — §C7.2.130
            Arm64Opcode.FMOV_GP_TO_FP => $"FMOV {FpPrecName()}{Rd}, {rn}",
            Arm64Opcode.FMOV_FP_TO_GP => $"FMOV {rd}, {FpPrecName()}{Rn}",
            // FP register-to-register move — §C7.2.132
            Arm64Opcode.FMOV_FP_REG => $"FMOV {FpPrecName()}{Rd}, {FpPrecName()}{Rn}",
            // FP immediate from encoded 8-bit — §C7.2.131
            Arm64Opcode.FMOV_FP_CONST => $"FMOV {FpPrecName()}{Rd}, #{DecodeFpImm8((byte)Immediate, Is64Bit)}",
            // FP conditional select — §C7.2.74
            Arm64Opcode.FCSEL => $"FCSEL {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}, {CondName(Condition)}",
            // SIMD vector zero — AdvSIMD modified immediate
            Arm64Opcode.MOVI_ZERO => Is64Bit
                ? $"MOVI V{Rd}.2D, #0000000000000000"
                : $"MOVI D{Rd}, #0000000000000000",
            // Conditional compare — §C6.2.46/C6.2.44
            Arm64Opcode.CCMP => $"CCMP {rn}, {rm}, #{Immediate & 0xF}, {CondName(Condition)}",
            Arm64Opcode.CCMN => $"CCMN {rn}, {rm}, #{Immediate & 0xF}, {CondName(Condition)}",
            Arm64Opcode.CCMP_IMM => $"CCMP {rn}, #{Rm}, #{Immediate & 0xF}, {CondName(Condition)}",
            Arm64Opcode.CCMN_IMM => $"CCMN {rn}, #{Rm}, #{Immediate & 0xF}, {CondName(Condition)}",
            // Wide multiply — §C6.2.229/C6.2.310
            Arm64Opcode.SMULL => $"SMULL X{Rd}, W{Rn}, W{Rm}",
            Arm64Opcode.UMULL => $"UMULL X{Rd}, W{Rn}, W{Rm}",
            Arm64Opcode.SMULH => $"SMULH X{Rd}, X{Rn}, X{Rm}",
            Arm64Opcode.UMULH => $"UMULH X{Rd}, X{Rn}, X{Rm}",
            // Variable shifts — §C6.2.153/C6.2.156
            Arm64Opcode.LSLV => $"LSL {rd}, {rn}, {rm}",
            Arm64Opcode.LSRV => $"LSR {rd}, {rn}, {rm}",
            Arm64Opcode.ASRV => $"ASR {rd}, {rn}, {rm}",
            Arm64Opcode.RORV => $"ROR {rd}, {rn}, {rm}",
            // Signed loads
            Arm64Opcode.LDRSB_IMM => $"{(IsUnscaled ? "LDURSB" : "LDRSB")} {rd}, {FmtMem(Rn, Immediate, Writeback)}",
            Arm64Opcode.LDRSH_IMM => $"{(IsUnscaled ? "LDURSH" : "LDRSH")} {rd}, {FmtMem(Rn, Immediate, Writeback)}",
            // FP rounding — §C7.2.77-82
            Arm64Opcode.FRINTN => $"FRINTN {FpPrecName()}{Rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FRINTP => $"FRINTP {FpPrecName()}{Rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FRINTM => $"FRINTM {FpPrecName()}{Rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FRINTZ => $"FRINTZ {FpPrecName()}{Rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FRINTA => $"FRINTA {FpPrecName()}{Rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FRINTX => $"FRINTX {FpPrecName()}{Rd}, {FpPrecName()}{Rn}",
            // FP min/max — §C7.2.91/C7.2.97
            Arm64Opcode.FMAXNM => $"FMAXNM {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}",
            Arm64Opcode.FMINNM => $"FMINNM {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}",
            Arm64Opcode.FMAX => $"FMAX {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}",
            Arm64Opcode.FMIN => $"FMIN {FpPrecName()}{Rd}, {FpPrecName()}{Rn}, {FpPrecName()}{Rm}",
            // FP conversion with rounding modes
            Arm64Opcode.FCVTNS => $"FCVTNS {rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FCVTNU => $"FCVTNU {rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FCVTPS => $"FCVTPS {rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FCVTPU => $"FCVTPU {rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FCVTMS => $"FCVTMS {rd}, {FpPrecName()}{Rn}",
            Arm64Opcode.FCVTMU => $"FCVTMU {rd}, {FpPrecName()}{Rn}",
            // AdvSIMD vector ops — decoded from raw encoding
            // Source: ARM ARM §C7.2.1 (three same), §C7.2.2 (three different),
            //         §C7.2.4 (scalar three same), §C7.2.93 (indexed element)
            Arm64Opcode.SIMD_VECTOR_OP => SimdMnemonicResolver.Resolve(RawValue)
                                          ?? $"V_OP V{Rd}, V{Rn}, V{Rm}",
            // AdvSIMD copy (DUP/INS/UMOV/SMOV)
            Arm64Opcode.SIMD_DUP => $"DUP V{Rd}.{VectorArrangement(Shift, Is64Bit)}, V{Rn}.{VectorElementSize(Shift)}[{VectorElementIndex(Shift)}]",
            Arm64Opcode.SIMD_DUP_GP => $"DUP V{Rd}.{VectorArrangement(Shift, Is64Bit)}, {GpReg(Rn, (Shift & 8) == 8, false)}",
            Arm64Opcode.SIMD_DUP_ELEMENT => $"MOV {DupElementDest(Rd, Shift)}, V{Rn}.{DupElementSrc(Shift)}",
            Arm64Opcode.SIMD_INS => $"INS V{Rd}.{VectorElementSize(Shift)}[{VectorElementIndex(Shift)}], " + 
                                    ((((RawValue >> 11) & 0xF) == 0b0011) 
                                        ? $"{GpReg(Rn, (Shift & 8) != 0 && (Shift & 7) == 0, false)}"
                                        : $"V{Rn}.{VectorElementSize(Shift)}[{VectorSrcElementIndex((byte)((RawValue >> 11) & 0xF), Shift)}]"),
            Arm64Opcode.SIMD_UMOV => $"UMOV {GpReg(Rd, Is64Bit, false)}, V{Rn}.{VectorElementSize(Shift)}[{VectorElementIndex(Shift)}]",
            // AdvSIMD scalar pairwise
            Arm64Opcode.SIMD_FADDP => $"FADDP {FpPrecName()}{Rd}, V{Rn}",
            // AdvSIMD shift by immediate
            Arm64Opcode.SIMD_SHL => $"SHL V{Rd}.{SimdVectorArrangement(Shift, Is64Bit)}, V{Rn}.{SimdVectorArrangement(Shift, Is64Bit)}, #{Shift - (1 << (SimdVectorSize(Shift) + 3))}",
            Arm64Opcode.SIMD_SSHR => $"SSHR V{Rd}.{SimdVectorArrangement(Shift, Is64Bit)}, V{Rn}.{SimdVectorArrangement(Shift, Is64Bit)}, #{(1 << (SimdVectorSize(Shift) + 4)) - Shift}",
            Arm64Opcode.SIMD_USHR => $"USHR V{Rd}.{SimdVectorArrangement(Shift, Is64Bit)}, V{Rn}.{SimdVectorArrangement(Shift, Is64Bit)}, #{(1 << (SimdVectorSize(Shift) + 4)) - Shift}",
            Arm64Opcode.SIMD_SSHLL => $"{(Is64Bit ? "SSHLL2" : "SSHLL")} V{Rd}.{WideArrangement(SimdVectorSize(Shift), Is64Bit)}, V{Rn}.{SimdVectorArrangement(Shift, Is64Bit)}, #{Shift - (1 << (SimdVectorSize(Shift) + 3))}",
            Arm64Opcode.SIMD_USHLL => $"{(Is64Bit ? "USHLL2" : "USHLL")} V{Rd}.{WideArrangement(SimdVectorSize(Shift), Is64Bit)}, V{Rn}.{SimdVectorArrangement(Shift, Is64Bit)}, #{Shift - (1 << (SimdVectorSize(Shift) + 3))}",
            // AdvSIMD modified immediate (non-zero)
            Arm64Opcode.SIMD_MOVI => FormatSimdMovi(Rd, Immediate, Shift, Is64Bit, RawValue),
            // AdvSIMD EXT
            Arm64Opcode.SIMD_EXT => $"EXT V{Rd}, V{Rn}, V{Rm}, #{Shift}",
            _ => $"??? 0x{RawValue:X8}"
        };
    }

    /// <summary>Returns "S" or "D" based on the Shift field encoding (2=S, 3=D) for FP instructions.</summary>
    private string FpPrecName() => Shift >= 3 ? "D" : "S";

    /// <summary>
    /// Decode 8-bit FP immediate to float/double string.
    ///
    /// Source Path: capstone-next/arch/AArch64/AArch64AddressingModes.h:440-459
    /// Original Snippet:
    ///   uint32_t Sign = (Imm >> 7) &amp; 0x1;
    ///   uint32_t Exp = (Imm >> 4) &amp; 0x7;
    ///   uint32_t Mantissa = Imm &amp; 0xf;
    ///   //   8-bit FP    IEEE Float Encoding
    ///   //   abcd efgh   aBbbbbbc defgh000 00000000 00000000
    ///   // where B = NOT(b);
    ///   I |= Sign &lt;&lt; 31;
    ///   I |= ((Exp &amp; 0x4) != 0 ? 0 : 1) &lt;&lt; 30;
    ///   I |= ((Exp &amp; 0x4) != 0 ? 0x1f : 0) &lt;&lt; 25;
    ///   I |= (Exp &amp; 0x3) &lt;&lt; 23;
    ///   I |= Mantissa &lt;&lt; 19;
    /// </summary>
    internal static string DecodeFpImm8(byte imm8, bool isDouble)
    {
        uint sign = ((uint)imm8 >> 7) & 0x1;
        uint exp  = ((uint)imm8 >> 4) & 0x7;
        uint mantissa = (uint)imm8 & 0xF;

        if (isDouble)
        {
            // Double: aBbbbbbbbb cdefgh00 00000000 ... (64-bit)
            // Source: ARM ARM C2.2.3
            ulong I = 0;
            I |= (ulong)sign << 63;
            I |= ((exp & 0x4) != 0 ? 0UL : 1UL) << 62;
            I |= ((exp & 0x4) != 0 ? 0xFFUL : 0UL) << 54;
            I |= (ulong)(exp & 0x3) << 52;
            I |= (ulong)mantissa << 48;
            double val = BitConverter.Int64BitsToDouble((long)I);
            return val.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            // Single: aBbbbbbc defgh000 00000000 00000000
            uint I = 0;
            I |= sign << 31;
            I |= ((exp & 0x4) != 0 ? 0U : 1U) << 30;
            I |= ((exp & 0x4) != 0 ? 0x1FU : 0U) << 25;
            I |= (exp & 0x3) << 23;
            I |= mantissa << 19;
            float val = BitConverter.Int32BitsToSingle((int)I);
            return val.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Decode ARM64 imm8 float encoding to raw IEEE754 bits for IR.</summary>
    internal static long DecodeFpImm8RawBits(byte imm8, bool isDouble)
    {
        uint sign = ((uint)imm8 >> 7) & 0x1;
        uint exp  = ((uint)imm8 >> 4) & 0x7;
        uint mantissa = (uint)imm8 & 0xF;

        if (isDouble)
        {
            ulong I = 0;
            I |= (ulong)sign << 63;
            I |= ((exp & 0x4) != 0 ? 0UL : 1UL) << 62;
            I |= ((exp & 0x4) != 0 ? 0xFFUL : 0UL) << 54;
            I |= (ulong)(exp & 0x3) << 52;
            I |= (ulong)mantissa << 48;
            return (long)I;
        }
        else
        {
            uint I = 0;
            I |= sign << 31;
            I |= ((exp & 0x4) != 0 ? 0U : 1U) << 30;
            I |= ((exp & 0x4) != 0 ? 0x1FU : 0U) << 25;
            I |= (exp & 0x3) << 23;
            I |= mantissa << 19;
            return (long)(uint)I; // Zero-extend to avoid sign-extension of negative float bits
        }
    }

    private static int SimdVectorSize(byte immh_immb)
    {
        if (immh_immb >= 64) return 3;
        if (immh_immb >= 32) return 2;
        if (immh_immb >= 16) return 1;
        return 0;
    }

    private static string SimdVectorArrangement(byte immh_immb, bool is64Bit)
    {
        int size = SimdVectorSize(immh_immb);
        return (is64Bit, size) switch
        {
            (false, 0) => "8B",
            (true, 0) => "16B",
            (false, 1) => "4H",
            (true, 1) => "8H",
            (false, 2) => "2S",
            (true, 2) => "4S",
            (true, 3) => "2D",
            _ => "?"
        };
    }

    private static string WideArrangement(int size, bool is64Bit)
    {
        return size switch
        {
            0 => "8H",
            1 => "4S",
            2 => "2D",
            _ => "?"
        };
    }

    private static string ShiftAmount(byte shiftType, byte shiftValue)
    {
        if (shiftValue == 0) return "";
        string typeStr = shiftType switch { 0 => "LSL", 1 => "LSR", 2 => "ASR", 3 => "ROR", _ => "LSL" };
        return $", {typeStr} #{shiftValue}";
    }

    /// <summary>SIMD register name from size encoded in Shift field: 0=B, 1=H, 2=S, 3=D, 4=Q</summary>
    private static string SimdRegName(byte size) => size switch
    {
        0 => "B", 1 => "H", 2 => "S", 3 => "D", 4 => "Q", _ => "V"
    };

    /// <summary>Decode imm5 for scalar DUP element → destination register name (e.g., "S1").
    /// ARM DDI: lowest set bit of imm5 determines element size.</summary>
    private static string DupElementDest(byte rd, byte imm5)
    {
        int lsb = imm5 & (-imm5);
        return lsb switch
        {
            1 => $"B{rd}", 2 => $"H{rd}", 4 => $"S{rd}", 8 => $"D{rd}", _ => $"S{rd}"
        };
    }

    /// <summary>Decode imm5 for scalar DUP element → source element spec (e.g., "S[1]").
    /// ARM DDI: lowest set bit = size, bits above = element index.</summary>
    private static string DupElementSrc(byte imm5)
    {
        int lsb = imm5 & (-imm5);
        return lsb switch
        {
            1 => $"B[{imm5 >> 1}]", 2 => $"H[{imm5 >> 2}]",
            4 => $"S[{imm5 >> 3}]", 8 => $"D[{imm5 >> 4}]", _ => $"S[0]"
        };
    }

    /// <summary>Condition code name for display.</summary>
    private static string CondName(byte cond) => (cond & 0xF) switch
    {
        0x0 => "EQ", 0x1 => "NE", 0x2 => "HS", 0x3 => "LO",
        0x4 => "MI", 0x5 => "PL", 0x6 => "VS", 0x7 => "VC",
        0x8 => "HI", 0x9 => "LS", 0xA => "GE", 0xB => "LT",
        0xC => "GT", 0xD => "LE", 0xE => "AL", _ => $"0x{cond:X}"
    };

    private string FormatLoadStoreRegOffset(bool isLoad)
    {
        uint size = (RawValue >> 30) & 0x3;
        uint opc = (RawValue >> 22) & 0x3;
        
        string op = isLoad ? "LDR" : "STR";
        string rtStr = GpReg(Rd, Is64Bit, false);

        if (size == 0)
        {
            if (opc == 0) op = "STRB";
            else if (opc == 1) op = "LDRB";
            else if (opc == 2) { op = "LDRSB"; rtStr = GpReg(Rd, true, false); }
            else if (opc == 3) { op = "LDRSB"; rtStr = GpReg(Rd, false, false); }
        }
        else if (size == 1)
        {
            if (opc == 0) op = "STRH";
            else if (opc == 1) op = "LDRH";
            else if (opc == 2) { op = "LDRSH"; rtStr = GpReg(Rd, true, false); }
            else if (opc == 3) { op = "LDRSH"; rtStr = GpReg(Rd, false, false); }
        }
        else if (size == 2)
        {
            if (opc == 0) op = "STR";
            else if (opc == 1) op = "LDR";
            else if (opc == 2) { op = "LDRSW"; rtStr = GpReg(Rd, true, false); }
            else if (opc == 3) op = "PRFM";
        }
        else if (size == 3)
        {
            if (opc == 0) op = "STR";
            else if (opc == 1) op = "LDR";
            else if (opc == 2) op = "PRFM";
        }
        
        if (op == "PRFM")
        {
            // PRFM ignores Rt formatting in standard disassembly (uses prefetch string)
            // But Capstone outputs `prfm pldl1keep, [x0, x1]`. We just use PRFM.
            return $"PRFM [{(Rn == 31 ? "SP" : $"X{Rn}")}, {FmtRegOffset(Rm, ExtendOption, IsScaled, Shift)}]";
        }
        
        return $"{op} {rtStr}, [{(Rn == 31 ? "SP" : $"X{Rn}")}, {FmtRegOffset(Rm, ExtendOption, IsScaled, Shift)}]";
    }

    private static string FormatBitfieldMove(string baseOp, int rd, int rn, int immr, int imms, bool is64Bit)
    {
        string rdStr = is64Bit ? $"X{rd}" : $"W{rd}";
        string rnStr = is64Bit ? $"X{rn}" : $"W{rn}";
        int maxBit = is64Bit ? 63 : 31;
        
        if (baseOp == "UBFM")
        {
            if (imms == maxBit && immr > 0) return $"LSR {rdStr}, {rnStr}, #{immr}";
            if (imms + 1 == immr) return $"LSL {rdStr}, {rnStr}, #{(maxBit + 1 - immr)}";
            if (imms < immr) return $"UBFIZ {rdStr}, {rnStr}, #{(maxBit + 1 - immr)}, #{imms + 1}";
            if (immr == 0 && imms == 7) return $"UXTB {rdStr}, W{rn}";
            if (immr == 0 && imms == 15) return $"UXTH {rdStr}, W{rn}";
            if (is64Bit && immr == 0 && imms == 31) return $"UXTW {rdStr}, W{rn}";
            return $"UBFX {rdStr}, {rnStr}, #{immr}, #{imms - immr + 1}";
        }
        else if (baseOp == "SBFM")
        {
            if (imms == maxBit && immr > 0) return $"ASR {rdStr}, {rnStr}, #{immr}";
            if (imms < immr) return $"SBFIZ {rdStr}, {rnStr}, #{(maxBit + 1 - immr)}, #{imms + 1}";
            if (immr == 0 && imms == 7) return $"SXTB {rdStr}, W{rn}";
            if (immr == 0 && imms == 15) return $"SXTH {rdStr}, W{rn}";
            if (is64Bit && immr == 0 && imms == 31) return $"SXTW {rdStr}, W{rn}";
            return $"SBFX {rdStr}, {rnStr}, #{immr}, #{imms - immr + 1}";
        }
        else if (baseOp == "BFM")
        {
            if (imms < immr) return $"BFI {rdStr}, {rnStr}, #{(maxBit + 1 - immr)}, #{imms + 1}";
            return $"BFXIL {rdStr}, {rnStr}, #{immr}, #{imms - immr + 1}";
        }
        return $"{baseOp} {rdStr}, {rnStr}, #{immr}, #{imms}";
    }
}

/// <summary>
/// ARM64 opcode families relevant to IL2CPP codegen patterns.
/// </summary>
public enum Arm64Opcode : byte
{
    Unknown = 0,

    // Move immediate
    MOVZ,       // Move wide with zero
    MOVK,       // Move wide with keep
    MOVN,       // Move wide with NOT

    // PC-relative addressing
    ADRP,       // Form PC-relative address to 4KB page
    ADR,        // Form PC-relative address

    // Arithmetic immediate
    ADD_IMM,    // Add immediate
    SUB_IMM,    // Subtract immediate

    // Load/Store unsigned immediate offset
    LDR_IMM,    // Load register (unsigned offset)
    STR_IMM,    // Store register (unsigned offset)
    LDRB_IMM,   // Load register byte
    STRB_IMM,   // Store register byte
    LDRH_IMM,   // Load register halfword
    STRH_IMM,   // Store register halfword
    LDRSW_IMM,  // Load register signed word (64-bit)
    LDR_LIT,    // Load register (literal, PC-relative)
    LDR_LIT_FP, // Load FP register (literal, PC-relative) — V=1 variant

    // Load/Store pair
    LDP,        // Load pair
    LDPSW,      // Load pair of signed words
    STP,        // Store pair

    // Branch
    B,          // Branch
    BL,         // Branch with link (call)
    BR,         // Branch to register
    BLR,        // Branch with link to register
    RET,        // Return from subroutine
    B_COND,     // Conditional branch
    CBZ,        // Compare and branch if zero
    CBNZ,       // Compare and branch if not zero
    TBZ,        // Test bit and branch if zero
    TBNZ,       // Test bit and branch if not zero

    // Register move
    MOV_REG,    // Move register (alias of ORR Xd, XZR, Xm)

    // Floating point
    FMOV_IMM,   // Floating-point move immediate
    FMOV_REG,   // Floating-point move register (general ↔ fp)

    // SIMD/FP Load/Store (V=1 in encoding)
    // Source: Clang codegen for Ldc_R4/R8 → LDR Sn/Dn from constant pool
    // Source: il2cpp_codegen_initobj → memset → Clang STP Qn (128-bit zero)
    LDR_SIMD_IMM,  // LDR Bn/Hn/Sn/Dn/Qn, [Xn, #imm] (FP/SIMD load)
    STR_SIMD_IMM,  // STR Bn/Hn/Sn/Dn/Qn, [Xn, #imm] (FP/SIMD store)
    LDR_SIMD_REG,  // LDR Bn/Hn/Sn/Dn/Qn, [Xn, Xm] (FP/SIMD register offset load)
    STR_SIMD_REG,  // STR Bn/Hn/Sn/Dn/Qn, [Xn, Xm] (FP/SIMD register offset store)
    LDP_SIMD,      // LDP Sn/Dn/Qn pair load
    STP_SIMD,      // STP Sn/Dn/Qn pair store

    // Data processing — register
    CMP_REG,    // CMP Xn, Xm (alias of SUBS XZR, Xn, Xm)
    CMP_IMM,    // CMP Xn, #imm (alias of SUBS XZR, Xn, #imm)
    SUBS_REG,   // SUBS Xd, Xn, Xm
    SUBS_IMM,   // SUBS Xd, Xn, #imm (sets flags)
    ADDS_IMM,   // ADDS Xd, Xn, #imm (sets flags)
    STR_REG,    // STR Xt, [Xn, Xm] (register offset)
    LDR_REG,    // LDR Xt, [Xn, Xm] (register offset)
    ADD_REG,    // ADD Xd, Xn, Xm (register add) — §C6.2.3
    SUB_REG,    // SUB Xd, Xn, Xm (register sub) — §C6.2.276
    ADDS_REG,   // ADDS Xd, Xn, Xm (register add, setting flags)

    // Logical immediate — §C6.2.12/C6.2.198
    // Source: Clang emits for bitmask ops (enum flags, alignment, hash shifts)
    AND_IMM,    // AND Xd, Xn, #bitmask
    ORR_IMM,    // ORR Xd, Xn, #bitmask
    EOR_IMM,    // EOR Xd, Xn, #bitmask
    ANDS_IMM,   // ANDS Xd, Xn, #bitmask (TST alias when Rd=XZR)

    // Logical register — §C6.2.13/C6.2.199
    AND_REG,    // AND Xd, Xn, Xm
    BIC_REG,    // BIC Xd, Xn, Xm
    ORR_REG,    // ORR Xd, Xn, Xm (MOV alias when Rn=XZR)
    ORN_REG,    // ORN Xd, Xn, Xm
    EOR_REG,    // EOR Xd, Xn, Xm
    EON_REG,    // EON Xd, Xn, Xm
    ANDS_REG,   // ANDS Xd, Xn, Xm
    BICS_REG,   // BICS Xd, Xn, Xm

    // Bitfield operations — §C6.2.316/C6.2.221
    // Source: Clang emits for shifts (LSL/LSR/ASR), sign-extend (SXTW), zero-extend (UXTB/UXTH)
    UBFM,       // Unsigned bitfield move (aliases: LSR, LSL, UXTB, UXTH)
    SBFM,       // Signed bitfield move (aliases: ASR, SXTW, SXTH, SXTB)
    EXTR,       // Extract/rotate register — §C6.2.91

    // Conditional select — §C6.2.66/C6.2.67/C6.2.68
    // Source: Clang emits for ternary (?:), boolean comparison results, conditional increment
    CSEL,       // Rd = cond ? Rn : Rm
    CSINC,      // Rd = cond ? Rn : Rm+1 (CSET alias when Rn=Rm=XZR)
    CSINV,      // Rd = cond ? Rn : ~Rm (CSETM alias when Rn=Rm=XZR)
    CSNEG,      // Rd = cond ? Rn : -Rm

    // Multiply / Divide — §C6.2.160/C6.2.162/C6.2.228/C6.2.316
    // Source: Clang emits for integer multiplication and division
    MADD,       // Rd = Ra + Rn*Rm (MUL alias when Ra=XZR)
    MSUB,       // Rd = Ra - Rn*Rm (MNEG alias when Ra=XZR)
    SDIV,       // Signed divide
    UDIV,       // Unsigned divide

    // Wide multiply — §C6.2.229/C6.2.310/C6.2.228/C6.2.309
    // Source: Clang emits for checked 64-bit multiply, il2cpp_codegen_check_mul_oveflow_u
    SMULL,      // Signed multiply long: Xd = Wn * Wm (widening)
    UMULL,      // Unsigned multiply long: Xd = Wn * Wm (widening)
    SMULH,      // Signed multiply high: Xd = (Xn * Xm) >> 64
    UMULH,      // Unsigned multiply high: Xd = (Xn * Xm) >> 64

    // Floating-point data-processing 2-source — §C7.2.67-150
    // Source: Clang for Code.Add/Sub/Mul/Div on float/double
    FADD,       // FP add
    FSUB,       // FP subtract
    FMUL,       // FP multiply
    FDIV,       // FP divide
    FNMUL,      // FP negate multiply

    // Floating-point compare — §C7.2.72
    // Source: Clang for Blt/Bgt/Beq on float operands
    FCMP,       // FP compare (Rm=0xFF means compare to zero)
    FCCMP,      // FP conditional compare

    // Floating-point data-processing 1-source — §C7.2.67-69
    // Source: Clang for Math.Abs, Code.Neg on floats, Math.Sqrt
    FABS,       // FP absolute value
    FNEG,       // FP negate
    FSQRT,      // FP square root

    // FP data-processing 3-source — §C7.2.100-103
    // Source: Clang for fused multiply-add patterns in math formulas
    FMADD,      // FMADD Sd, Sn, Sm, Sa → Sd = Sa + Sn * Sm
    FMSUB,      // FMSUB Sd, Sn, Sm, Sa → Sd = Sa - Sn * Sm
    FNMADD,     // FNMADD Sd, Sn, Sm, Sa → Sd = -(Sa + Sn * Sm)
    FNMSUB,     // FNMSUB Sd, Sn, Sm, Sa → Sd = -(Sa - Sn * Sm)

    // FP conversion between precisions — §C7.2.71
    // Source: Clang for Conv_R4↔Conv_R8 mixed precision
    FCVT_PREC,  // Convert between single and double (Is64Bit=true: S→D)

    // FP ↔ integer conversion — §C7.2.73-80
    // Source: Clang for Conv_I4/Conv_R4 casts
    FCVTZS,     // FP to signed int (truncate toward zero)
    FCVTZU,     // FP to unsigned int (truncate toward zero)
    SCVTF,      // Signed int to FP
    UCVTF,      // Unsigned int to FP

    // FP ↔ GP register move — §C7.2.130
    // Source: Clang for bitcast float↔int (reinterpret_cast)
    FMOV_GP_TO_FP,  // FMOV Sn/Dn, Wn/Xn (GP → FP)
    FMOV_FP_TO_GP,  // FMOV Wn/Xn, Sn/Dn (FP → GP)

    // FP register-to-register move — §C7.2.132
    FMOV_FP_REG,    // FMOV Sn, Sm / FMOV Dn, Dm

    // FP immediate from encoded 8-bit — §C7.2.131
    // Source: Clang for Ldc_R4 common constants (1.0, 0.5, etc.)
    FMOV_FP_CONST,  // FMOV Sn, #imm8 / FMOV Dn, #imm8

    // FP conditional select — §C7.2.74
    // Source: Clang for ternary (a > b ? x : y) on floats
    FCSEL,          // FCSEL Sn, Sm, Sp, cond

    // SIMD/AdvSIMD modified immediate — vector zero
    // Source: Clang for initobj / memset(0) / struct zero-init
    MOVI_ZERO,      // MOVI Vn.2D, #0 or MOVI Dn, #0

    // Conditional compare — §C6.2.46/C6.2.44
    // Source: Clang for complex if (a && b) chains
    CCMP,       // Conditional compare (register)
    CCMN,       // Conditional compare negative (register)
    CCMP_IMM,   // Conditional compare (immediate)
    CCMN_IMM,   // Conditional compare negative (immediate)

    // Bitfield move — §C6.2.24
    // Source: Clang for BFXIL, BFI insertion/extraction patterns
    BFM,        // Bitfield move (aliases: BFI, BFXIL)

    // Variable shifts (register) — §C6.2.153/C6.2.156/C6.2.14/C6.2.217
    // Source: MethodBodyWriter.cs L1553-1560: Code.Shl/Shr → C <</>>→ Clang LSLV/LSRV
    LSLV,       // Logical shift left variable
    LSRV,       // Logical shift right variable
    ASRV,       // Arithmetic shift right variable
    RORV,       // Rotate right variable

    // Signed byte/halfword loads — §C6.2.131/C6.2.135
    // Source: IL2CPP byte/short field access with sign-extension
    LDRSB_IMM,  // Load register signed byte (unsigned offset)
    LDRSH_IMM,  // Load register signed halfword (unsigned offset)

    // FP rounding — §C7.2.77-82
    // Source: il2cpp-codegen.h L1498: bankers_roundf → Clang FRINTN
    // Source: Math.Floor → FRINTM, Math.Ceiling → FRINTP, Math.Truncate → FRINTZ
    FRINTN,     // Round to nearest, ties to even
    FRINTP,     // Round toward +∞ (ceiling)
    FRINTM,     // Round toward -∞ (floor)
    FRINTZ,     // Round toward zero (truncate)
    FRINTA,     // Round to nearest, ties away from zero
    FRINTX,     // Round to integral exact (signals inexact)

    // FP min/max — §C7.2.91/C7.2.97
    // Source: Math.Max/Math.Min on float/double
    FMAXNM,     // FP max number (NaN-propagating)
    FMINNM,     // FP min number (NaN-propagating)
    FMAX,       // FP max
    FMIN,       // FP min

    // FP conversion with rounding modes — §C7.2.73-76
    // Source: Clang for specific rounding modes in conv patterns
    FCVTNS,     // FP to signed int, round to nearest
    FCVTNU,     // FP to unsigned int, round to nearest
    FCVTPS,     // FP to signed int, round toward +∞
    FCVTPU,     // FP to unsigned int, round toward +∞
    FCVTMS,     // FP to signed int, round toward -∞
    FCVTMU,     // FP to unsigned int, round toward -∞

    // AdvSIMD vector three-same — §C7.2.1/C7.2.116
    // Source: Clang for Vector2/3/4 math in Unity
    SIMD_VECTOR_OP, // Generic vector op (ADD/SUB/FADD/FMUL etc.)

    // AdvSIMD copy — §C7.2.33/C7.2.117
    // Source: Clang for vector element access (v.x, v.y, etc.)
    SIMD_DUP,   // DUP (element)
    SIMD_DUP_GP,// DUP (general — from GP register to SIMD vector)
    SIMD_DUP_ELEMENT, // DUP/MOV scalar from element — extracts one element to scalar register
    SIMD_INS,   // INS (element to element, or GP to element)
    SIMD_UMOV,  // UMOV (unsigned move from element to GP)

    // AdvSIMD scalar pairwise — §C7.2.46
    // Source: Clang for dot product / sum reduction
    SIMD_FADDP, // FADDP (scalar) — add pair of elements

    // AdvSIMD shift by immediate — §C7.2.220/C7.2.224
    // Source: Clang for vector shift operations
    SIMD_SHL,   // SHL (vector shift left by immediate)
    SIMD_SSHR,  // SSHR (vector shift right by immediate)
    SIMD_USHR,  // USHR (vector shift right by immediate)
    SIMD_SSHLL, // SSHLL (vector shift left by immediate and widen)
    SIMD_USHLL, // USHLL (vector shift left by immediate and widen)

    // AdvSIMD modified immediate (non-zero) — §C7.2.175
    // Source: Clang for vector constant loads
    SIMD_MOVI,  // MOVI (non-zero vector immediate)

    // AdvSIMD EXT — §C7.2.43
    // Source: Clang for vector shuffle/permute
    SIMD_EXT,   // EXT (extract from pair of vectors)

    // System
    MRS,        // Move system register to general register (e.g. TPIDR_EL0)

    // No-op
    NOP,
}
