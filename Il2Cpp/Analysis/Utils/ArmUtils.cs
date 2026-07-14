using Rosetta.Binary;

namespace Rosetta.Analysis.Utils;

/// <summary>Generic utility methods for ARM64 architecture analysis.</summary>
public static class ArmUtils
{
    /// <summary>Check if an ARM64 register is the stack pointer (SP) or frame pointer (X29).</summary>
    public static bool IsStackPointer(long regNum) => regNum == 31 || regNum == 13;

    /// <summary>Get the standard ARM register prefix (X, W, S, D, Q) based on width and type.</summary>
    public static string GetRegisterPrefix(int regNum, byte bitWidth, bool isArm32)
    {
        if (regNum >= 32)
        {
            // Floating point / SIMD registers
            return bitWidth switch
            {
                8 or 16 or 32 => "s",
                64 => "d",
                128 => "q",
                _ => "v"
            };
        }

        if (isArm32) return "r";
        return bitWidth == 64 ? "x" : "w";
    }

    /// <summary>Check if an opcode typically writes to its Rd register operand.</summary>
    public static bool IsWriteToRd(Arm64Opcode op) => op switch
    {
        Arm64Opcode.MOVZ or Arm64Opcode.MOVK or Arm64Opcode.MOVN => true,
        Arm64Opcode.MOV_REG or Arm64Opcode.ADR or Arm64Opcode.ADRP => true,
        Arm64Opcode.ADD_IMM or Arm64Opcode.SUB_IMM => true,
        Arm64Opcode.ADD_REG or Arm64Opcode.SUB_REG => true,
        Arm64Opcode.LDR_IMM or Arm64Opcode.LDR_REG or Arm64Opcode.LDR_LIT => true,
        Arm64Opcode.LDRB_IMM or Arm64Opcode.LDRH_IMM or Arm64Opcode.LDRSW_IMM => true,
        Arm64Opcode.CSEL or Arm64Opcode.CSINC or Arm64Opcode.CSINV or Arm64Opcode.CSNEG => true,
        Arm64Opcode.MADD or Arm64Opcode.MSUB or Arm64Opcode.SDIV or Arm64Opcode.UDIV => true,
        Arm64Opcode.AND_IMM or Arm64Opcode.ORR_IMM or Arm64Opcode.EOR_IMM => true,
        Arm64Opcode.AND_REG or Arm64Opcode.ORR_REG or Arm64Opcode.EOR_REG => true,
        Arm64Opcode.UBFM or Arm64Opcode.SBFM or Arm64Opcode.EXTR => true,
        Arm64Opcode.SMULL or Arm64Opcode.UMULL => true,
        Arm64Opcode.BL or Arm64Opcode.BLR => true, // X0 = return value
        _ => false,
    };
}
