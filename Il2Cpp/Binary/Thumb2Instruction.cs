namespace Rosetta.Binary;

/// <summary>
/// Decoded ARM32 Thumb2 instruction.
///
/// Source evidence:
///   ARMv7-M Architecture Reference Manual (DDI 0403)
///   ARMv7-A/R Architecture Reference Manual (DDI 0406)
///   §A6 Thumb instruction set encoding
///
/// Thumb2 instructions are 16-bit or 32-bit:
///   - 16-bit: hw0[15:11] < 0b11101
///   - 32-bit: hw0[15:11] >= 0b11101, followed by hw1
///
/// Only instruction families emitted by Clang for IL2CPP codegen are decoded:
///   - PUSH/POP (prologue/epilogue)
///   - MOV/MOVW/MOVT (immediate loads)
///   - ADD/SUB (arithmetic)
///   - LDR/STR variants (memory)
///   - CMP/TST (comparison)
///   - BL/BLX/B/B.cond/CBZ/CBNZ (branches)
///   - VFP (floating-point via coprocessor)
///   - IT blocks (conditional execution)
/// </summary>
public readonly struct Thumb2Instruction
{
    /// <summary>Raw 16-bit or 32-bit instruction (little-endian, zero-padded for 16-bit).</summary>
    public readonly uint RawValue;

    /// <summary>Virtual address of this instruction.</summary>
    public readonly ulong Address;

    /// <summary>Decoded opcode family.</summary>
    public readonly Thumb2Opcode Opcode;

    /// <summary>Destination/data register (Rd or Rt), 0-15.</summary>
    public readonly byte Rd;

    /// <summary>First source/base register (Rn), 0-15.</summary>
    public readonly byte Rn;

    /// <summary>Second source register (Rm), 0-15.</summary>
    public readonly byte Rm;

    /// <summary>Immediate value (sign-extended where appropriate).</summary>
    public readonly long Immediate;

    /// <summary>Shift amount or extra data.</summary>
    public readonly byte Shift;

    /// <summary>Condition code for conditional instructions (0-14).</summary>
    public readonly byte Condition;

    /// <summary>Instruction size in bytes (2 or 4).</summary>
    public readonly byte Size;

    /// <summary>Register list bitmask for PUSH/POP/LDM/STM.</summary>
    public readonly ushort RegisterList;

    public Thumb2Instruction(uint raw, ulong address, Thumb2Opcode opcode,
        byte rd = 0, byte rn = 0, byte rm = 0, long immediate = 0,
        byte shift = 0, byte condition = 14, byte size = 2,
        ushort registerList = 0)
    {
        RawValue = raw;
        Address = address;
        Opcode = opcode;
        Rd = rd;
        Rn = rn;
        Rm = rm;
        Immediate = immediate;
        Shift = shift;
        Condition = condition;
        Size = size;
        RegisterList = registerList;
    }

    /// <summary>R13 = SP, R14 = LR, R15 = PC.</summary>
    private static string GpReg(byte reg) => reg switch
    {
        13 => "SP", 14 => "LR", 15 => "PC",
        _ => $"R{reg}"
    };

    /// <summary>Format a signed immediate.</summary>
    private static string FmtImm(long imm)
    {
        if (imm < 0) return $"#-0x{-imm:X}";
        return $"#0x{imm:X}";
    }

    /// <summary>Format register list for PUSH/POP.</summary>
    private string FmtRegList()
    {
        var parts = new System.Collections.Generic.List<string>();
        for (int r = 0; r <= 15; r++)
            if ((RegisterList & (1 << r)) != 0)
                parts.Add(GpReg((byte)r));
        return "{" + string.Join(", ", parts) + "}";
    }

    public override string ToString()
    {
        string rd = GpReg(Rd);
        string rn = GpReg(Rn);
        string rm = GpReg(Rm);

        return Opcode switch
        {
            Thumb2Opcode.NOP => "NOP",
            Thumb2Opcode.PUSH => $"PUSH {FmtRegList()}",
            Thumb2Opcode.POP => $"POP {FmtRegList()}",
            Thumb2Opcode.MOV_IMM => $"MOV {rd}, {FmtImm(Immediate)}",
            Thumb2Opcode.MOV_REG => $"MOV {rd}, {rm}",
            Thumb2Opcode.MOVW => $"MOVW {rd}, {FmtImm(Immediate)}",
            Thumb2Opcode.MOVT => $"MOVT {rd}, {FmtImm(Immediate)}",
            Thumb2Opcode.ADD_IMM => $"ADD {rd}, {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.ADD_REG => $"ADD {rd}, {rn}, {rm}",
            Thumb2Opcode.SUB_IMM => $"SUB {rd}, {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.SUB_REG => $"SUB {rd}, {rn}, {rm}",
            Thumb2Opcode.ADC_IMM => $"ADC {rd}, {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.ADC_REG => $"ADC {rd}, {rn}, {rm}",
            Thumb2Opcode.SBC_IMM => $"SBC {rd}, {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.SBC_REG => $"SBC {rd}, {rn}, {rm}",
            Thumb2Opcode.MUL => $"MUL {rd}, {rn}, {rm}",
            Thumb2Opcode.MLA => $"MLA {rd}, {rn}, {rm}",
            Thumb2Opcode.SDIV => $"SDIV {rd}, {rn}, {rm}",
            Thumb2Opcode.UDIV => $"UDIV {rd}, {rn}, {rm}",
            Thumb2Opcode.AND_IMM => $"AND {rd}, {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.AND_REG => $"AND {rd}, {rn}, {rm}",
            Thumb2Opcode.ORR_IMM => $"ORR {rd}, {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.ORR_REG => $"ORR {rd}, {rn}, {rm}",
            Thumb2Opcode.EOR_IMM => $"EOR {rd}, {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.EOR_REG => $"EOR {rd}, {rn}, {rm}",
            Thumb2Opcode.BIC_IMM => $"BIC {rd}, {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.BIC_REG => $"BIC {rd}, {rn}, {rm}",
            Thumb2Opcode.LSL_IMM => $"LSL {rd}, {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.LSL_REG => $"LSL {rd}, {rn}, {rm}",
            Thumb2Opcode.LSR_IMM => $"LSR {rd}, {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.LSR_REG => $"LSR {rd}, {rn}, {rm}",
            Thumb2Opcode.ASR_IMM => $"ASR {rd}, {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.ASR_REG => $"ASR {rd}, {rn}, {rm}",
            Thumb2Opcode.MVN_IMM => $"MVN {rd}, {FmtImm(Immediate)}",
            Thumb2Opcode.MVN_REG => $"MVN {rd}, {rm}",
            Thumb2Opcode.NEG => $"NEG {rd}, {rm}",
            Thumb2Opcode.CMP_IMM => $"CMP {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.CMP_REG => $"CMP {rn}, {rm}",
            Thumb2Opcode.TST_IMM => $"TST {rn}, {FmtImm(Immediate)}",
            Thumb2Opcode.TST_REG => $"TST {rn}, {rm}",
            Thumb2Opcode.LDR_IMM => $"LDR {rd}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.STR_IMM => $"STR {rd}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.LDRB_IMM => $"LDRB {rd}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.STRB_IMM => $"STRB {rd}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.LDRH_IMM => $"LDRH {rd}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.STRH_IMM => $"STRH {rd}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.LDRSB_IMM => $"LDRSB {rd}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.LDRSH_IMM => $"LDRSH {rd}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.LDR_REG => $"LDR {rd}, [{rn}, {rm}]",
            Thumb2Opcode.STR_REG => $"STR {rd}, [{rn}, {rm}]",
            Thumb2Opcode.LDR_LIT => $"LDR {rd}, [PC, {FmtImm(Immediate)}]",
            Thumb2Opcode.LDRD_IMM => $"LDRD {rd}, {rm}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.STRD_IMM => $"STRD {rd}, {rm}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.B => $"B #0x{(ulong)Immediate:X}",
            Thumb2Opcode.B_COND => $"B.{CondName(Condition)} #0x{(ulong)Immediate:X}",
            Thumb2Opcode.BL => $"BL #0x{(ulong)Immediate:X}",
            Thumb2Opcode.BLX => $"BLX #0x{(ulong)Immediate:X}",
            Thumb2Opcode.BLX_REG => $"BLX {rm}",
            Thumb2Opcode.BX => $"BX {rm}",
            Thumb2Opcode.CBZ => $"CBZ {rd}, #0x{(ulong)Immediate:X}",
            Thumb2Opcode.CBNZ => $"CBNZ {rd}, #0x{(ulong)Immediate:X}",
            Thumb2Opcode.IT => $"IT{new string('T', Shift - 1)} {CondName(Condition)}",
            // VFP
            Thumb2Opcode.VLDR => $"VLDR {(Shift >= 64 ? "D" : "S")}{Rd}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.VSTR => $"VSTR {(Shift >= 64 ? "D" : "S")}{Rd}, [{rn}, {FmtImm(Immediate)}]",
            Thumb2Opcode.VMOV_IMM => $"VMOV {(Shift >= 64 ? "D" : "S")}{Rd}, #imm",
            Thumb2Opcode.VMOV_REG => $"VMOV {(Shift >= 64 ? "D" : "S")}{Rd}, {(Shift >= 64 ? "D" : "S")}{Rm}",
            Thumb2Opcode.VMOV_GP_TO_FP => $"VMOV S{Rd}, {rn}",
            Thumb2Opcode.VMOV_FP_TO_GP => $"VMOV {rd}, S{Rn}",
            Thumb2Opcode.VADD => $"VADD.F{Shift} {(Shift >= 64 ? "D" : "S")}{Rd}, {(Shift >= 64 ? "D" : "S")}{Rn}, {(Shift >= 64 ? "D" : "S")}{Rm}",
            Thumb2Opcode.VSUB => $"VSUB.F{Shift} {(Shift >= 64 ? "D" : "S")}{Rd}, {(Shift >= 64 ? "D" : "S")}{Rn}, {(Shift >= 64 ? "D" : "S")}{Rm}",
            Thumb2Opcode.VMUL => $"VMUL.F{Shift} {(Shift >= 64 ? "D" : "S")}{Rd}, {(Shift >= 64 ? "D" : "S")}{Rn}, {(Shift >= 64 ? "D" : "S")}{Rm}",
            Thumb2Opcode.VDIV => $"VDIV.F{Shift} {(Shift >= 64 ? "D" : "S")}{Rd}, {(Shift >= 64 ? "D" : "S")}{Rn}, {(Shift >= 64 ? "D" : "S")}{Rm}",
            Thumb2Opcode.VNEG => $"VNEG.F{Shift} {(Shift >= 64 ? "D" : "S")}{Rd}, {(Shift >= 64 ? "D" : "S")}{Rm}",
            Thumb2Opcode.VABS => $"VABS.F{Shift} {(Shift >= 64 ? "D" : "S")}{Rd}, {(Shift >= 64 ? "D" : "S")}{Rm}",
            Thumb2Opcode.VSQRT => $"VSQRT.F{Shift} {(Shift >= 64 ? "D" : "S")}{Rd}, {(Shift >= 64 ? "D" : "S")}{Rm}",
            Thumb2Opcode.VCMP => $"VCMP.F{Shift} {(Shift >= 64 ? "D" : "S")}{Rn}, {(Shift >= 64 ? "D" : "S")}{Rm}",
            Thumb2Opcode.VCVT_F32_S32 => $"VCVT.F32.S32 S{Rd}, S{Rm}",
            Thumb2Opcode.VCVT_S32_F32 => $"VCVT.S32.F32 S{Rd}, S{Rm}",
            Thumb2Opcode.VCVT_F64_F32 => $"VCVT.F64.F32 D{Rd}, S{Rm}",
            Thumb2Opcode.VCVT_F32_F64 => $"VCVT.F32.F64 S{Rd}, D{Rm}",
            Thumb2Opcode.VMRS => "VMRS APSR_nzcv, FPSCR",
            Thumb2Opcode.DMB => "DMB ISH",
            _ => $"??? 0x{RawValue:X8}"
        };
    }

    private static string CondName(byte cond) => (cond & 0xF) switch
    {
        0x0 => "EQ", 0x1 => "NE", 0x2 => "HS", 0x3 => "LO",
        0x4 => "MI", 0x5 => "PL", 0x6 => "VS", 0x7 => "VC",
        0x8 => "HI", 0x9 => "LS", 0xA => "GE", 0xB => "LT",
        0xC => "GT", 0xD => "LE", 0xE => "AL", _ => $"0x{cond:X}"
    };
}

/// <summary>
/// Thumb2 opcode families relevant to IL2CPP codegen patterns.
/// </summary>
public enum Thumb2Opcode : byte
{
    Unknown = 0,

    // Prologue/Epilogue
    PUSH,
    POP,

    // Data movement
    MOV_IMM,
    MOV_REG,
    MOVW,       // 16-bit wide immediate (bottom half)
    MOVT,       // 16-bit top immediate (top half)

    // Arithmetic
    ADD_IMM,
    ADD_REG,
    SUB_IMM,
    SUB_REG,
    ADC_IMM,    // Add with carry
    ADC_REG,
    SBC_IMM,    // Subtract with carry
    SBC_REG,
    MUL,
    MLA,        // Multiply-accumulate
    SDIV,
    UDIV,

    // Bitwise
    AND_IMM,
    AND_REG,
    ORR_IMM,
    ORR_REG,
    EOR_IMM,
    EOR_REG,
    BIC_IMM,
    BIC_REG,
    LSL_IMM,
    LSL_REG,
    LSR_IMM,
    LSR_REG,
    ASR_IMM,
    ASR_REG,
    MVN_IMM,
    MVN_REG,
    NEG,        // RSB Rd, Rm, #0

    // Comparison
    CMP_IMM,
    CMP_REG,
    TST_IMM,
    TST_REG,

    // Memory — immediate offset
    LDR_IMM,
    STR_IMM,
    LDRB_IMM,
    STRB_IMM,
    LDRH_IMM,
    STRH_IMM,
    LDRSB_IMM,
    LDRSH_IMM,

    // Memory — register offset
    LDR_REG,
    STR_REG,

    // Memory — PC-relative literal
    LDR_LIT,

    // Memory — double-word
    LDRD_IMM,
    STRD_IMM,

    // Branches
    B,
    B_COND,
    BL,
    BLX,
    BLX_REG,   // BLX <reg> — register-indirect call (saves LR, non-terminator)
    BX,
    CBZ,
    CBNZ,

    // IT block
    IT,

    // VFP (floating-point coprocessor)
    VLDR,
    VSTR,
    VMOV_IMM,
    VMOV_REG,
    VMOV_GP_TO_FP,
    VMOV_FP_TO_GP,
    VADD,
    VSUB,
    VMUL,
    VDIV,
    VNEG,
    VABS,
    VSQRT,
    VCMP,
    VCVT_F32_S32,   // int → float
    VCVT_S32_F32,   // float → int
    VCVT_F64_F32,   // float → double
    VCVT_F32_F64,   // double → float
    VCVT_U32_F32,   // float → uint
    VCVT_F32_U32,   // uint → float
    VMRS,           // Move FP status to APSR

    // System
    DMB,            // Data memory barrier
    NOP,
}
