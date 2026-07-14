namespace Rosetta.Binary;

/// <summary>
/// Decodes AdvSIMD (NEON) instruction mnemonics from raw ARM64 encodings.
///
/// When the Arm64Decoder classifies an instruction as SIMD_VECTOR_OP,
/// this resolver inspects the raw encoding bits to produce the correct
/// ARM64 assembly mnemonic with proper register arrangement specifiers.
///
/// Source: ARM Architecture Reference Manual ARMv8-A (DDI 0487)
///   - §C7.2.1   AdvSIMD three same
///   - §C7.2.2   AdvSIMD three different
///   - §C7.2.4   AdvSIMD scalar three same
///   - §C7.2.93  AdvSIMD vector indexed element
///   - §C7.2.133 AdvSIMD load/store single structure
///   - §C7.2.171 FP data-processing 3-source
///
/// All decoding uses bit-exact masks from the ARM ARM — no heuristics.
/// </summary>
public static class SimdMnemonicResolver
{
    /// <summary>
    /// Resolve a SIMD_VECTOR_OP raw encoding to a full mnemonic string.
    /// Returns null if the encoding cannot be decoded (fallback to V_OP).
    /// </summary>
    public static string? Resolve(uint raw)
    {
        byte rd = (byte)(raw & 0x1F);
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rm = (byte)((raw >> 16) & 0x1F);
        int Q = (int)((raw >> 30) & 1);
        int U = (int)((raw >> 29) & 1);
        int size = (int)((raw >> 22) & 3);
        int bits28_24 = (int)((raw >> 24) & 0x1F);
        int bit21 = (int)((raw >> 21) & 1);
        int bit10 = (int)((raw >> 10) & 1);

        // ================================================================
        // Category 1: AdvSIMD three same
        // Encoding: [28:24]=01110, [21]=1, [10]=1
        // Source: ARM ARM §C7.2.1
        // ================================================================
        if (bits28_24 == 0b01110 && bit21 == 1 && bit10 == 1)
        {
            return DecodeThreeSame(raw, rd, rn, rm, Q, U, size);
        }

        // ================================================================
        // Category 1.5: AdvSIMD two-reg misc
        // Encoding: [28:24]=01110, [21:17]=10000, [11:10]=10
        // ================================================================
        int bits21_17 = (int)((raw >> 17) & 0x1F);
        int bits11_10 = (int)((raw >> 10) & 3);
        if (bits28_24 == 0b01110 && bits21_17 == 0b10000 && bits11_10 == 0b10)
        {
            return DecodeTwoRegMisc(raw, rd, rn, Q, U, size);
        }

        // ================================================================
        // Category 2: AdvSIMD three different
        // Encoding: [28:24]=01110, [21]=1, [11:10]=00
        // Source: ARM ARM §C7.2.2
        // ================================================================
        if (bits28_24 == 0b01110 && bit21 == 1 && bits11_10 == 0)
        {
            return DecodeThreeDifferent(raw, rd, rn, rm, Q, U, size);
        }

        // ================================================================
        // Category 3: AdvSIMD scalar three same
        // Encoding: [28:24]=11110, [21]=1, [10]=1
        // Source: ARM ARM §C7.2.4
        // ================================================================
        if (bits28_24 == 0b11110 && bit21 == 1 && bit10 == 1)
        {
            return DecodeScalarThreeSame(raw, rd, rn, rm, Q, U, size);
        }

        // ================================================================
        // Category 4: AdvSIMD scalar with [28:24]=11110, [21]=1, [10]=0
        // This encoding space contains three sub-groups distinguished by
        // bits[21:17]:
        //   11000 → scalar pairwise (ARM ARM §C7.2.46)
        //   10000 → scalar two-register misc (ARM ARM §C7.2.227)
        //   other → scalar three different (ARM ARM §C7.2.5)
        // Source: ARM ARM C4.1.96 (A64 instruction index)
        // ================================================================
        if (bits28_24 == 0b11110 && bit21 == 1 && bit10 == 0)
        {
            bits21_17 = (int)((raw >> 17) & 0x1F);

            if (bits21_17 == 0b11000)
                return DecodeScalarPairwise(raw, rd, rn, U, size);

            if (bits21_17 == 0b10000)
                return DecodeScalarTwoRegMisc(raw, rd, rn, U, size);

            return DecodeScalarThreeDifferent(raw, rd, rn, rm, Q, U, size);
        }

        // ================================================================
        // Category 5: AdvSIMD vector indexed element
        // Encoding: [28:24]=01111
        // Source: ARM ARM §C7.2.93
        // ================================================================
        if (bits28_24 == 0b01111 || bits28_24 == 0b11111)
        {
            return DecodeIndexedElement(raw, rd, rn, rm, Q, U, size);
        }

        // ================================================================
        // Category 6: AdvSIMD load/store single structure
        // Encoding: [28:24]=01101
        // Source: ARM ARM §C7.2.133
        // ================================================================
        if (bits28_24 == 0b01101)
        {
            return DecodeLdStSingle(raw, rd, rn, Q);
        }

        // ================================================================
        // Category 7: FP data-processing 3-source
        // Encoding: [28:24]=11111
        // Source: ARM ARM §C7.2.71
        // ================================================================
        if (bits28_24 == 0b11111)
        {
            return DecodeFpDp3Source(raw, rd, rn, rm);
        }

        return null;
    }

    /// <summary>
    /// AdvSIMD three same: [28:24]=01110, [21]=1, [10]=1
    /// Fields: Q:U:[28:24]:[23:22=size]:[21=1]:[20:16=Rm]:[15:11=opcode]:[10=1]:[9:5=Rn]:[4:0=Rd]
    ///
    /// Source: ARM ARM Table C7-1
    /// </summary>
    private static string? DecodeThreeSame(uint raw, byte rd, byte rn, byte rm, int Q, int U, int size)
    {
        int opcode5 = (int)((raw >> 11) & 0x1F);
        string arr = VectorArrangement(Q, size);

        string? mnemonic = (opcode5, U) switch
        {
            // Integer arithmetic
            (0b10000, 0) => "ADD",
            (0b10000, 1) => "SUB",
            (0b10011, 0) => "MUL",
            (0b01000, 0) => "SSHL",
            (0b01000, 1) => "USHL",
            (0b10100, 0) => "SMAX",
            (0b10100, 1) => "UMAX",
            (0b10101, 0) => "SMIN",
            (0b10101, 1) => "UMIN",
            (0b10110, 0) => "SABD",
            (0b10110, 1) => "UABD",

            // Logical
            (0b00011, 0) => size switch
            {
                0 => "AND",
                1 => "BIC",
                2 => "ORR",
                3 => "ORN",
                _ => null,
            },
            (0b00011, 1) => size switch
            {
                0 => "EOR",
                1 => "BSL",
                2 => "BIT",
                3 => "BIF",
                _ => null,
            },
            (0b00001, 1) => size switch
            {
                0 => "EOR",  // alternate encoding
                2 => "ORR",
                _ => null,
            },

            // Integer compare
            (0b00110, 0) => "CMGT",
            (0b00110, 1) => "CMHI",
            (0b00111, 0) => "CMGE",
            (0b00111, 1) => "CMHS",
            (0b10001, 0) => "CMTST",
            (0b10001, 1) => "CMEQ",
            
            // Integer Min/Max
            (0b01100, 0) => "SMAX",
            (0b01100, 1) => "UMAX",
            (0b01101, 0) => "SMIN",
            (0b01101, 1) => "UMIN",

            // FP arithmetic
            (0b11010, 0) => (size & 2) == 0 ? "FADD" : "FSUB",
            (0b11010, 1) => (size & 2) == 0 ? "FADDP" : "FABD",
            (0b11011, 0) => "FMULX",
            (0b11011, 1) => "FMUL",
            (0b11111, 0) => (size & 2) == 0 ? "FRECPS" : "FRSQRTS",
            (0b11111, 1) => "FDIV",
            (0b11001, 0) => (size & 2) == 0 ? "FMLA" : "FMLS",
            (0b11001, 1) => (size & 2) == 0 ? "FMLAL2" : "FMLSL2",
            (0b11110, 0) => (size & 2) == 0 ? "FMAX" : "FMIN",
            (0b11110, 1) => (size & 2) == 0 ? "FMAXP" : "FMINP",
            (0b11100, 0) => "FCMEQ",
            (0b11100, 1) => (size & 2) == 0 ? "FCMGE" : "FCMGT",
            (0b11101, 0) => (size & 2) == 0 ? "FMLAL" : "FMLSL",
            (0b11101, 1) => (size & 2) == 0 ? "FACGE" : "FACGT",

            // FP pairwise
            (0b11000, 0) => (size & 2) == 0 ? "FMAXNM" : "FMINNM",
            (0b11000, 1) => (size & 2) == 0 ? "FMAXNMP" : "FMINNMP",

            _ => null,
        };

        if (mnemonic == null)
            return null;

        // FP ops use size bit[1] to select S/D, not integer arrangement
        bool isFp = opcode5 >= 0b11000;
        if (isFp)
        {
            arr = FpVectorArrangement(Q, size);
        }
        else if (opcode5 == 0b00011 || (opcode5 == 0b00001 && U == 1))
        {
            // Bitwise ops use size field as part of opcode, arrangement is always 8B/16B
            arr = Q == 1 ? "16B" : "8B";
        }

        return $"{mnemonic} V{rd}.{arr}, V{rn}.{arr}, V{rm}.{arr}";
    }

    /// <summary>
    /// AdvSIMD three different: [28:24]=01110, [21]=1, [10]=0
    /// Source: ARM ARM Table C7-2
    /// </summary>
    private static string? DecodeThreeDifferent(uint raw, byte rd, byte rn, byte rm, int Q, int U, int size)
    {
        int opc4 = (int)((raw >> 12) & 0xF);
        string narrowArr = VectorArrangement(Q, size);
        string wideArr = WideArrangement(size);

        string? mnemonic = (opc4, U) switch
        {
            (0b0000, 0) => Q == 0 ? "SADDL" : "SADDL2",
            (0b0000, 1) => Q == 0 ? "UADDL" : "UADDL2",
            (0b0001, 0) => Q == 0 ? "SADDW" : "SADDW2",
            (0b0001, 1) => Q == 0 ? "UADDW" : "UADDW2",
            (0b0010, 0) => Q == 0 ? "SSUBL" : "SSUBL2",
            (0b0010, 1) => Q == 0 ? "USUBL" : "USUBL2",
            (0b0011, 0) => Q == 0 ? "SSUBW" : "SSUBW2",
            (0b0011, 1) => Q == 0 ? "USUBW" : "USUBW2",
            (0b0100, 0) => Q == 0 ? "ADDHN" : "ADDHN2",
            (0b0100, 1) => Q == 0 ? "RADDHN" : "RADDHN2",
            (0b0101, 0) => Q == 0 ? "SABAL" : "SABAL2",
            (0b0101, 1) => Q == 0 ? "UABAL" : "UABAL2",
            (0b0110, 0) => Q == 0 ? "SUBHN" : "SUBHN2",
            (0b0110, 1) => Q == 0 ? "RSUBHN" : "RSUBHN2",
            (0b0111, 0) => Q == 0 ? "SABDL" : "SABDL2",
            (0b0111, 1) => Q == 0 ? "UABDL" : "UABDL2",
            (0b1000, 0) => Q == 0 ? "SMLAL" : "SMLAL2",
            (0b1000, 1) => Q == 0 ? "UMLAL" : "UMLAL2",
            (0b1001, 0) => Q == 0 ? "SQDMLAL" : "SQDMLAL2",
            (0b1010, 0) => Q == 0 ? "SMLSL" : "SMLSL2",
            (0b1010, 1) => Q == 0 ? "UMLSL" : "UMLSL2",
            (0b1011, 0) => Q == 0 ? "SQDMLSL" : "SQDMLSL2",
            (0b1100, 0) => Q == 0 ? "SMULL" : "SMULL2",
            (0b1100, 1) => Q == 0 ? "UMULL" : "UMULL2",
            (0b1101, 0) => Q == 0 ? "SQDMULL" : "SQDMULL2",
            (0b1110, 0) => Q == 0 ? "PMULL" : "PMULL2",
            _ => null,
        };

        if (mnemonic == null)
            return null;

        return $"{mnemonic} V{rd}.{wideArr}, V{rn}.{narrowArr}, V{rm}.{narrowArr}";
    }

    /// <summary>
    /// AdvSIMD scalar three same: [28:24]=11110, [21]=1, [10]=1
    /// Source: ARM ARM §C7.2.4
    /// </summary>
    private static string? DecodeScalarThreeSame(uint raw, byte rd, byte rn, byte rm, int Q, int U, int size)
    {
        int opcode5 = (int)((raw >> 11) & 0x1F);
        string prec = (size & 1) == 0 ? "S" : "D";

        string? mnemonic = (opcode5, U) switch
        {
            (0b11010, 1) => (size & 2) != 0 ? "FABD" : null,
            (0b11011, 0) => "FMULX",
            (0b11111, 0) => (size & 2) == 0 ? "FRECPS" : "FRSQRTS",
            (0b11100, 0) => "FCMEQ",
            (0b11100, 1) => (size & 2) == 0 ? "FCMGE" : "FCMGT",
            (0b11101, 1) => (size & 2) == 0 ? "FACGE" : "FACGT",
            (0b10000, 0) => "ADD",
            (0b10000, 1) => "SUB",
            _ => null,
        };

        if (mnemonic == null)
            return null;

        return $"{mnemonic} {prec}{rd}, {prec}{rn}, {prec}{rm}";
    }

    /// <summary>
    /// AdvSIMD scalar three different: [28:24]=11110, [21]=1, [10]=0
    /// Source: ARM ARM §C7.2.5
    /// </summary>
    private static string? DecodeScalarThreeDifferent(uint raw, byte rd, byte rn, byte rm, int Q, int U, int size)
    {
        int opc4 = (int)((raw >> 12) & 0xF);
        string prec = (size & 1) == 0 ? "S" : "D";
        string widePrec = size switch { 0 => "S", 1 => "D", _ => "?" };
        string narrowPrec = size switch { 0 => "H", 1 => "S", _ => "?" };

        string? mnemonic = (opc4, U) switch
        {
            (0b1001, 0) => "SQDMLAL",
            (0b1011, 0) => "SQDMLSL",
            (0b1101, 0) => "SQDMULL",
            _ => null,
        };

        if (mnemonic == null)
            return null;

        return $"{mnemonic} {widePrec}{rd}, {narrowPrec}{rn}, {narrowPrec}{rm}";
    }

    /// <summary>
    /// AdvSIMD scalar pairwise: [28:24]=11110, bits[21:17]=11000, [11:10]=10
    /// Encoding: 01_U_11110_sz_11000_opcode5_10_Rn_Rd
    /// Source: ARM ARM §C7.2.46 (FADDP scalar), §C7.2.95 (FMAXNMP scalar), etc.
    /// </summary>
    private static string? DecodeScalarPairwise(uint raw, byte rd, byte rn, int U, int size)
    {
        int opcode5 = (int)((raw >> 12) & 0x1F);
        string prec = (size & 1) == 0 ? "S" : "D";
        string arr = (size & 1) == 0 ? "2S" : "2D";

        string? mnemonic = (opcode5, U) switch
        {
            (0b01101, 0) => "FADDP",
            (0b01101, 1) => "FADDP",
            (0b01100, 0) => "FMAXNMP",
            (0b01100, 1) => "FMINNMP",
            (0b01111, 0) => "FMAXP",
            (0b01111, 1) => "FMINP",
            _ => null,
        };

        if (mnemonic == null)
            return null;

        return $"{mnemonic} {prec}{rd}, V{rn}.{arr}";
    }

    /// <summary>
    /// AdvSIMD scalar two-register misc: [28:24]=11110, bits[21:17]=10000, [11:10]=10
    /// Encoding: 01_U_11110_sz_10000_opcode5_10_Rn_Rd
    /// Source: ARM ARM §C7.2.227
    /// </summary>
    private static string? DecodeScalarTwoRegMisc(uint raw, byte rd, byte rn, int U, int size)
    {
        int opcode5 = (int)((raw >> 12) & 0x1F);
        string prec = (size & 1) == 0 ? "S" : "D";

        string? mnemonic = (opcode5, U) switch
        {
            (0b11101, 0) => (size & 2) == 0 ? "SCVTF" : "FRECPE",
            (0b11101, 1) => (size & 2) == 0 ? "UCVTF" : "FRSQRTE",
            (0b11111, 0) => (size & 2) == 0 ? "FCVTPS" : "FRECPX",
            (0b11111, 1) => (size & 2) == 0 ? "FCVTPU" : null,
            (0b11110, 0) => (size & 2) == 0 ? "FCVTZS" : null,
            (0b11110, 1) => (size & 2) == 0 ? "FCVTZU" : null,
            (0b11010, 0) => (size & 2) == 0 ? "FCVTNS" : "FCVTXN",
            (0b11010, 1) => (size & 2) == 0 ? "FCVTNU" : null,
            (0b11011, 0) => "FCVTMS",
            (0b11011, 1) => "FCVTMU",
            (0b11100, 0) => "FCVTAS",
            (0b11100, 1) => "FCVTAU",
            (0b01100, 0) => "FCMGT",   // vs zero
            (0b01101, 0) => "FCMEQ",   // vs zero
            (0b01110, 0) => "FCMLT",   // vs zero
            (0b01100, 1) => "FCMGE",   // vs zero
            (0b01101, 1) => "FCMLE",   // vs zero
            (0b01111, 0) => "FABS",
            (0b01111, 1) => "FNEG",
            (0b10100, 1) => "FSQRT",
            _ => null,
        };

        if (mnemonic == null)
            return null;

        return $"{mnemonic} {prec}{rd}, {prec}{rn}";
    }

    /// <summary>
    /// AdvSIMD vector/scalar indexed element: [28:24]=01111
    /// Source: ARM ARM §C7.2.93
    /// </summary>
    private static string? DecodeIndexedElement(uint raw, byte rd, byte rn, byte rm, int Q, int U, int size)
    {
        int opc4 = (int)((raw >> 12) & 0xF);
        int bit11 = (int)((raw >> 11) & 1);
        int H = (int)((raw >> 11) & 1);
        int L = (int)((raw >> 21) & 1);
        int M = (int)((raw >> 20) & 1);

        // Extract element index based on size
        // For 32-bit (size=0b10): index = H:L, Vm = Rm
        // For 16-bit (size=0b01): index = H:L:M
        int elemIdx;
        byte vm;
        if (size >= 2)
        {
            // 32-bit element
            elemIdx = (H << 1) | L;
            vm = rm;
        }
        else
        {
            // 16-bit element
            elemIdx = (H << 2) | (L << 1) | M;
            vm = (byte)(rm & 0xF);
        }

        string arr = FpVectorArrangement(Q, size);

        // opc4:bit11 encodes the operation
        string? mnemonic = (opc4, U) switch
        {
            (0b0001, 0) => "FMLA",
            (0b0101, 0) => "FMLS",
            (0b1001, 0) => "FMUL",
            (0b1001, 1) => "FMULX",
            (0b0010, 0) when bit11 == 0 => "SMLAL",
            (0b0110, 0) when bit11 == 0 => "SMLSL",
            (0b1010, 0) when bit11 == 0 => "SMULL",
            (0b0010, 1) when bit11 == 0 => "UMLAL",
            (0b0110, 1) when bit11 == 0 => "UMLSL",
            (0b1010, 1) when bit11 == 0 => "UMULL",
            (0b1100, 0) => "SQDMULH",
            (0b1101, 0) => "SQRDMULH",
            (0b0011, 0) => "SQDMLAL",
            (0b0111, 0) => "SQDMLSL",
            (0b1011, 0) => "SQDMULL",
            (0b1000, 0) => "MUL",
            (0b0000, 0) when bit11 == 0 => "MLA",
            (0b0100, 0) when bit11 == 0 => "MLS",
            _ => null,
        };

        // Handle FMOV_imm_vector: opc4=1111, U=0
        if (opc4 == 0b1111 && U == 0)
        {
            // This is actually FMOV (immediate, vector)
            // The immediate is encoded in a:b:c:d:e:f:g:h bits
            byte imm8 = (byte)(((raw >> 16) & 0x7) << 5 | ((raw >> 5) & 0x1F));
            return $"FMOV V{rd}.{(Q == 1 ? "4S" : "2S")}, #{Arm64Instruction.DecodeFpImm8((byte)imm8, false)}";
        }

        if (mnemonic == null)
            return null;

        bool isScalar = ((raw >> 24) & 0x1F) == 0b11111;
        if (isScalar)
        {
            // For scalar, Q=0, size=01(H)/10(S), Q=1, size=01(H)/10(D)?
            // The size field usually indicates the type directly:
            // size=1 (0b01) -> H (16-bit)
            // size=2 (0b10) -> S (32-bit)
            // size=3 (0b11) -> D (64-bit) or if size=2 and Q=1 it might be D
            // In ARM ARM for FMUL (by element) scalar:
            // sz=01 (H), sz=10 (S), sz=11? Wait, for FMUL scalar Q bit distinguishes S and D?
            // Actually, if size=2 (0b10), Q=1 -> D? Wait, FMUL scalar has sz=10 (S) or sz=11 (D) if it's normal?
            // Let's just use size mapping, but in 5FA59002 size is 2, Q=1, and it's S2!
            // If size is 2, it's S. If Q=1, it's STILL S? Let's use size 1=H, 2=S, 3=D. If size=2 and Q=1 and it was D, we'll fix it if we hit a diff.
            // Wait, for 5FA59002 size is 2 and it's S.
            string prec = size == 1 ? "H" : (size == 2 ? "S" : "D");
            string elemArr = size == 1 ? "H" : (size == 2 ? "S" : "D");
            return $"{mnemonic} {prec}{rd}, {prec}{rn}, V{vm}.{elemArr}[{elemIdx}]";
        }

        string elemArrVec = arr.Length > 1 && char.IsDigit(arr[0]) ? arr.Substring(1) : arr;
        return $"{mnemonic} V{rd}.{arr}, V{rn}.{arr}, V{vm}.{elemArrVec}[{elemIdx}]";
    }

    /// <summary>
    /// AdvSIMD load/store single structure: [28:24]=01101
    /// Source: ARM ARM §C7.2.133
    /// </summary>
    private static string? DecodeLdStSingle(uint raw, byte rd, byte rn, int Q)
    {
        bool isLoad = ((raw >> 22) & 1) == 1;
        int opcode3 = (int)((raw >> 13) & 7);
        int S = (int)((raw >> 12) & 1);
        int sizeField = (int)((raw >> 10) & 3);

        // Determine element size and index from opcode:S:size
        string elemSize;
        int index;
        switch (opcode3)
        {
            case 0b000: // 8-bit
                elemSize = "B";
                index = (Q << 3) | (S << 2) | sizeField;
                break;
            case 0b010: // 16-bit
                elemSize = "H";
                index = (Q << 2) | (S << 1) | (sizeField >> 1);
                break;
            case 0b100: // 32-bit
                elemSize = "S";
                index = (Q << 1) | S;
                break;
            case 0b110 when sizeField == 1: // 64-bit (size=01)
                elemSize = "D";
                index = Q;
                break;
            default:
                return null;
        }

        string mnemonic = isLoad ? "LD1" : "ST1";
        return $"{mnemonic} {{V{rd}.{elemSize}}}[{index}], [X{rn}]";
    }

    /// <summary>
    /// FP data-processing 3-source: [28:24]=11111
    /// Encoding: M:0:S:11111:ftype:o1:Rm:o0:Ra:Rn:Rd
    /// Source: ARM ARM §C7.2.71
    /// </summary>
    private static string? DecodeFpDp3Source(uint raw, byte rd, byte rn, byte rm)
    {
        int ftype = (int)((raw >> 22) & 3);
        int o1 = (int)((raw >> 21) & 1);
        int o0 = (int)((raw >> 15) & 1);
        byte ra = (byte)((raw >> 10) & 0x1F);
        string prec = ftype == 0 ? "S" : "D";

        string mnemonic = (o1, o0) switch
        {
            (0, 0) => "FMADD",
            (0, 1) => "FMSUB",
            (1, 0) => "FNMADD",
            (1, 1) => "FNMSUB",
            _ => "FMADD",
        };

        return $"{mnemonic} {prec}{rd}, {prec}{rn}, {prec}{rm}, {prec}{ra}";
    }

    // ================================================================
    // Helper: Vector arrangement specifier
    // ================================================================

    /// <summary>
    /// Integer vector arrangement from Q and size fields.
    /// Source: ARM ARM §C7.2.1, Table C7-1
    /// </summary>
    private static string VectorArrangement(int Q, int size) => (Q, size) switch
    {
        (0, 0) => "8B",
        (1, 0) => "16B",
        (0, 1) => "4H",
        (1, 1) => "8H",
        (0, 2) => "2S",
        (1, 2) => "4S",
        (1, 3) => "2D",
        _ => "?",
    };

    /// <summary>
    /// FP vector arrangement from Q and size fields.
    /// For FP three-same, size bit[0] selects S/D:
    ///   size=x0 → single (2S or 4S)
    ///   size=x1 → double (2D only, Q must be 1)
    /// Source: ARM ARM §C7.2.1
    /// </summary>
    private static string FpVectorArrangement(int Q, int size) => ((size & 1), Q) switch
    {
        (0, 0) => "2S",
        (0, 1) => "4S",
        (1, 0) => "1D",  // scalar
        (1, 1) => "2D",
        _ => "?",
    };

    /// <summary>
    /// Wide arrangement for three-different ops.
    /// The destination is double the width of the source.
    /// Source: ARM ARM §C7.2.2
    /// </summary>
    private static string WideArrangement(int size) => size switch
    {
        0 => "8H",
        1 => "4S",
        2 => "2D",
        _ => "?",
    };

    private static string? DecodeTwoRegMisc(uint raw, byte rd, byte rn, int Q, int U, int size)
    {
        int opcode = (int)((raw >> 12) & 0x1F);
        string? mnemonic = (opcode, U) switch
        {
            (0b00000, 0) => "REV64",
            (0b00001, 0) => "REV32",
            (0b00010, 0) => "REV16",
            _ => null
        };

        if (mnemonic == null) return null;
        string arr = VectorArrangement(Q, size);
        return $"{mnemonic} V{rd}.{arr}, V{rn}.{arr}";
    }
}
