using System.Buffers.Binary;
using Rosetta.Pipeline;

namespace Rosetta.Binary;

/// <summary>
/// ARM64 (AArch64) instruction decoder.
///
/// Decodes raw 32-bit instruction words into <see cref="Arm64Instruction"/> structs.
/// Only instruction families emitted by Clang for IL2CPP codegen are handled.
///
/// Source: ARM Architecture Reference Manual ARMv8-A (DDI 0487)
///   §C4.1  — Top-level encoding groups
///   §C4.1.2 — Data processing — immediate
///   §C4.1.3 — Branches, exception generating and system instructions
///   §C4.1.4 — Loads and stores
///
/// All ARM64 instructions are exactly 4 bytes, little-endian.
/// </summary>
public static class Arm64Decoder
{
    /// <summary>
    /// Decode a sequence of ARM64 instructions from raw bytes.
    /// </summary>
    /// <param name="data">Raw ELF .text segment bytes.</param>
    /// <param name="baseVA">Virtual address of the first byte in <paramref name="data"/>.</param>
    /// <param name="maxInstructions">Maximum number of instructions to decode (0 = decode all).</param>
    /// <returns>Array of decoded instructions.</returns>
    public static Arm64Instruction[] DecodeBlock(ReadOnlySpan<byte> data, ulong baseVA, int maxInstructions = 0)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"Arm64Decoder.DecodeBlock(baseVA=0x{baseVA:X})");
        int count = data.Length / 4;
        if (maxInstructions > 0 && maxInstructions < count)
            count = maxInstructions;

        var result = new Arm64Instruction[count];
        for (int i = 0; i < count; i++)
        {
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4, 4));
            ulong addr = baseVA + (ulong)(i * 4);
            result[i] = Decode(raw, addr);
        }
        return result;
    }

    /// <summary>
    /// Decode a single 32-bit ARM64 instruction word.
    /// </summary>
    public static Arm64Instruction Decode(uint raw, ulong address)
    {
        // §C4.1: Top-level decode by bits [28:25]
        uint op0 = (raw >> 25) & 0xF;

        // NOP: 0xD503201F
        if (raw == 0xD503201F)
            return new Arm64Instruction(raw, address, Arm64Opcode.NOP);

        // Zero-word padding (alignment bytes) — treat as NOP
        if (raw == 0x00000000)
            return new Arm64Instruction(raw, address, Arm64Opcode.NOP);

        // Check for RET first (0xD65F03C0)
        if ((raw & 0xFFFFFC1F) == 0xD65F0000)
        {
            byte rn = (byte)((raw >> 5) & 0x1F);
            return new Arm64Instruction(raw, address, Arm64Opcode.RET, rn: rn, is64Bit: true);
        }

        // ── System register read (MRS) ──
        // DDI 0487 §C5.2.2: MRS Xt, <sysreg>
        // Encoding: 1101 0101 0011 op1:3 CRn:4 CRm:4 op2:3 Rt:5
        // Fixed bits [31:20] = 1101 0101 0011 = 0xD53
        if ((raw >> 20) == 0xD53)
        {
            byte rt = (byte)(raw & 0x1F);
            // Pack sysreg encoding into immediate for identification
            long sysreg = (raw >> 5) & 0x7FFF;
            return new Arm64Instruction(raw, address, Arm64Opcode.MRS,
                rd: rt, immediate: sysreg, is64Bit: true);
        }

        // ── Branches, exception generating, system ──
        // op0 = 101x
        if ((op0 & 0xE) == 0xA)
            return DecodeBranch(raw, address);

        // ── Data processing — immediate ──
        // op0 = 100x
        if ((op0 & 0xE) == 0x8)
            return DecodeDataProcessingImmediate(raw, address);

        // ── Loads and stores ──
        // op0 = x1x0
        if ((op0 & 0x5) == 0x4)
            return DecodeLoadStore(raw, address);

        // ── Data processing — register ──
        // op0 = x101 (logic/arith register)
        if ((op0 & 0x7) == 0x5)
            return DecodeDataProcessingRegister(raw, address);

        // ── SIMD / Floating-point data processing ──
        // op0 = x111
        if ((op0 & 0x7) == 0x7)
            return DecodeSIMDFP(raw, address);

        return new Arm64Instruction(raw, address, Arm64Opcode.Unknown);
    }

    // ================================================================
    // Data Processing — Immediate
    // §C4.1.2
    // ================================================================

    private static Arm64Instruction DecodeDataProcessingImmediate(uint raw, ulong address)
    {
        // bits [25:23] = op0 for DP-imm subgroup
        uint dpOp0 = (raw >> 23) & 0x7;

        switch (dpOp0)
        {
            case 0b000: // PC-rel addressing (ADR/ADRP)
            case 0b001:
                return DecodeAdrp(raw, address);

            case 0b010: // Add/subtract immediate
            case 0b011:
                return DecodeAddSubImmediate(raw, address);

            case 0b100: // Logical immediate (AND/ORR/EOR/ANDS)
                return DecodeLogicalImmediate(raw, address);

            case 0b101: // Move wide immediate (MOVZ/MOVK/MOVN)
                return DecodeMoveWide(raw, address);

            case 0b110: // Bitfield (UBFM/SBFM/BFM)
                return DecodeBitfield(raw, address);

            case 0b111: // Extract (EXTR)
                return DecodeExtract(raw, address);

            default:
                return new Arm64Instruction(raw, address, Arm64Opcode.Unknown);
        }
    }

    /// <summary>
    /// ADRP/ADR — PC-relative address
    /// §C6.2.11 ADRP: immhi:immlo → page offset from PC
    ///
    /// Encoding: [31] op | [30:29] immlo | [28:24] 10000 | [23:5] immhi | [4:0] Rd
    /// ADRP: op=1, result = (PC & ~0xFFF) + (imm << 12)
    /// ADR:  op=0, result = PC + imm
    /// </summary>
    private static Arm64Instruction DecodeAdrp(uint raw, ulong address)
    {
        bool isAdrp = ((raw >> 31) & 1) == 1;
        byte rd = (byte)(raw & 0x1F);

        uint immlo = (raw >> 29) & 0x3;
        uint immhi = (raw >> 5) & 0x7FFFF; // 19 bits
        long imm = (long)((immhi << 2) | immlo);

        // Sign-extend from 21 bits
        if ((imm & (1L << 20)) != 0)
            imm |= unchecked((long)0xFFFFFFFFFFE00000L);

        long result;
        if (isAdrp)
        {
            imm <<= 12; // page granularity
            result = (long)(address & ~0xFFFUL) + imm;
        }
        else
        {
            result = (long)address + imm;
        }

        return new Arm64Instruction(raw, address,
            isAdrp ? Arm64Opcode.ADRP : Arm64Opcode.ADR,
            rd: rd, immediate: result, is64Bit: true);
    }

    /// <summary>
    /// ADD/SUB immediate
    /// §C6.2.4: [31] sf | [30] op | [29] S | [28:24] 10001 | [23:22] shift | [21:10] imm12 | [9:5] Rn | [4:0] Rd
    /// op=0 → ADD, op=1 → SUB
    /// shift: 00 = LSL #0, 01 = LSL #12
    /// </summary>
    private static Arm64Instruction DecodeAddSubImmediate(uint raw, ulong address)
    {
        bool is64 = ((raw >> 31) & 1) == 1;
        bool isSub = ((raw >> 30) & 1) == 1;
        bool setFlags = ((raw >> 29) & 1) == 1; // S flag — ADDS/SUBS set NZCV flags
        uint sh = (raw >> 22) & 0x3;
        uint imm12 = (raw >> 10) & 0xFFF;
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rd = (byte)(raw & 0x1F);

        long imm = (long)imm12;
        if (sh == 1) imm <<= 12;

        // Determine opcode based on op (SUB/ADD) and S flag
        // Source: ARM ARM §C6.2.4:
        //   op=0, S=0 → ADD;   op=0, S=1 → ADDS
        //   op=1, S=0 → SUB;   op=1, S=1 → SUBS
        //   SUBS with Rd=XZR (31) → CMP alias
        Arm64Opcode opcode;
        if (isSub && setFlags)
        {
            opcode = (rd == 31) ? Arm64Opcode.CMP_IMM : Arm64Opcode.SUBS_IMM;
        }
        else if (!isSub && setFlags)
        {
            opcode = Arm64Opcode.ADDS_IMM;
        }
        else
        {
            opcode = isSub ? Arm64Opcode.SUB_IMM : Arm64Opcode.ADD_IMM;
        }

        return new Arm64Instruction(raw, address, opcode,
            rd: rd, rn: rn, immediate: imm, is64Bit: is64);
    }

    /// <summary>
    /// Move wide immediate (MOVZ / MOVK / MOVN)
    /// §C6.2.191 MOVZ: [31] sf | [30:29] opc | [28:23] 100101 | [22:21] hw | [20:5] imm16 | [4:0] Rd
    /// opc: 00=MOVN, 10=MOVZ, 11=MOVK
    /// hw: shift amount / 16 (0,1,2,3 → shift 0,16,32,48)
    /// </summary>
    private static Arm64Instruction DecodeMoveWide(uint raw, ulong address)
    {
        bool is64 = ((raw >> 31) & 1) == 1;
        uint opc = (raw >> 29) & 0x3;
        byte hw = (byte)((raw >> 21) & 0x3);
        ushort imm16 = (ushort)((raw >> 5) & 0xFFFF);
        byte rd = (byte)(raw & 0x1F);

        byte shift = (byte)(hw * 16);

        Arm64Opcode opcode = opc switch
        {
            0b00 => Arm64Opcode.MOVN,
            0b10 => Arm64Opcode.MOVZ,
            0b11 => Arm64Opcode.MOVK,
            _ => Arm64Opcode.Unknown,
        };

        return new Arm64Instruction(raw, address, opcode,
            rd: rd, immediate: imm16, is64Bit: is64, shift: shift);
    }

    /// <summary>
    /// Logical immediate (AND/ORR/EOR/ANDS)
    /// §C6.2.12/C6.2.198/C6.2.87/C6.2.14:
    /// [31] sf | [30:29] opc | [28:23] 100100 | [22] N | [21:16] immr | [15:10] imms | [9:5] Rn | [4:0] Rd
    /// opc: 00=AND, 01=ORR, 10=EOR, 11=ANDS (TST alias when Rd=XZR)
    ///
    /// The bitmask is encoded as (N, immr, imms) per DecodeBitMasks() in the ARM ARM.
    /// Source: ARM ARM §K14.2 — AArch64 DecodeBitMasks
    /// </summary>
    private static Arm64Instruction DecodeLogicalImmediate(uint raw, ulong address)
    {
        bool is64 = ((raw >> 31) & 1) == 1;
        uint opc = (raw >> 29) & 0x3;
        uint N = (raw >> 22) & 1;
        uint immr = (raw >> 16) & 0x3F;
        uint imms = (raw >> 10) & 0x3F;
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rd = (byte)(raw & 0x1F);

        // Decode bitmask value
        long bitmask = (long)DecodeBitMask(N, imms, immr, is64);

        Arm64Opcode opcode = opc switch
        {
            0b00 => Arm64Opcode.AND_IMM,
            0b01 => Arm64Opcode.ORR_IMM,
            0b10 => Arm64Opcode.EOR_IMM,
            0b11 => Arm64Opcode.ANDS_IMM, // TST alias when Rd=31
            _ => Arm64Opcode.Unknown
        };

        return new Arm64Instruction(raw, address, opcode,
            rd: rd, rn: rn, immediate: bitmask, is64Bit: is64);
    }

    /// <summary>
    /// Decode a logical bitmask immediate value from (N, imms, immr).
    /// Source: ARM ARM §K14.2 — DecodeBitMasks()
    /// This is the standard algorithm for AArch64 logical immediate encoding.
    /// </summary>
    private static ulong DecodeBitMask(uint N, uint imms, uint immr, bool is64)
    {
        // Determine the element size
        int len = HighestSetBit((N << 6) | (~imms & 0x3F), 7);
        if (len < 1) return 0; // invalid encoding

        int size = 1 << len;
        uint levels = (uint)(size - 1);

        uint s = imms & levels;
        uint r = immr & levels;

        // Create the base element: (s+1) ones
        ulong welem = (s + 1 >= 64) ? ulong.MaxValue : ((1UL << (int)(s + 1)) - 1);

        // Rotate right by r
        if (r != 0)
            welem = (welem >> (int)r) | (welem << (size - (int)r));
        welem &= (size >= 64) ? ulong.MaxValue : ((1UL << size) - 1);

        // Replicate element across register width
        ulong result = 0;
        int width = is64 ? 64 : 32;
        for (int i = 0; i < width; i += size)
            result |= welem << i;

        if (!is64)
            result &= 0xFFFFFFFF;

        return result;
    }

    /// <summary>Find the highest set bit in a value up to bitCount bits.</summary>
    private static int HighestSetBit(uint value, int bitCount)
    {
        for (int i = bitCount - 1; i >= 0; i--)
        {
            if (((value >> i) & 1) == 1)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Bitfield move (SBFM/UBFM/BFM)
    /// §C6.2.221/C6.2.316/C6.2.27:
    /// [31] sf | [30:29] opc | [28:23] 100110 | [22] N | [21:16] immr | [15:10] imms | [9:5] Rn | [4:0] Rd
    /// opc: 00=SBFM, 01=BFM, 10=UBFM
    ///
    /// Aliases: SBFM → ASR, SXTW, SXTH, SXTB
    ///          UBFM → LSR, LSL, UXTB, UXTH
    /// </summary>
    private static Arm64Instruction DecodeBitfield(uint raw, ulong address)
    {
        bool is64 = ((raw >> 31) & 1) == 1;
        uint opc = (raw >> 29) & 0x3;
        byte immr = (byte)((raw >> 16) & 0x3F);
        byte imms = (byte)((raw >> 10) & 0x3F);
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rd = (byte)(raw & 0x1F);

        Arm64Opcode opcode = opc switch
        {
            0b00 => Arm64Opcode.SBFM,
            0b01 => Arm64Opcode.BFM,   // BFM aliases: BFI, BFXIL
            0b10 => Arm64Opcode.UBFM,
            _ => Arm64Opcode.Unknown
        };

        // Pack immr into Rm field and imms into Shift field for the handler
        return new Arm64Instruction(raw, address, opcode,
            rd: rd, rn: rn, rm: immr, is64Bit: is64, shift: imms);
    }

    /// <summary>
    /// Extract (EXTR/ROR)
    /// §C6.2.91: [31] sf | [30:29] 00 | [28:23] 100111 | [22] N | [21] 0 | [20:16] Rm | [15:10] imms | [9:5] Rn | [4:0] Rd
    /// When Rn=Rm, EXTR is the ROR alias.
    /// </summary>
    private static Arm64Instruction DecodeExtract(uint raw, ulong address)
    {
        bool is64 = ((raw >> 31) & 1) == 1;
        byte rm = (byte)((raw >> 16) & 0x1F);
        byte imms = (byte)((raw >> 10) & 0x3F);
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rd = (byte)(raw & 0x1F);

        return new Arm64Instruction(raw, address, Arm64Opcode.EXTR,
            rd: rd, rn: rn, rm: rm, is64Bit: is64, shift: imms);
    }

    // ================================================================
    // Loads and Stores
    // §C4.1.4
    // ================================================================

    private static Arm64Instruction DecodeLoadStore(uint raw, ulong address)
    {
        uint bits2927 = (raw >> 27) & 0x7;
        uint V = (raw >> 26) & 1;
        uint bits2524 = (raw >> 24) & 0x3;

        // ── LDR literal (GP): [29:27] = 011, [25:24] = 00, V=0 ──
        if (bits2927 == 0b011 && bits2524 == 0b00 && V == 0)
            return DecodeLdrLiteral(raw, address);

        // ── LDR literal (FP/SIMD): [29:27] = 011, [25:24] = 00, V=1 ──
        // DDI 0487 §C7.2.175: LDR St/Dt/Qt, <label>
        // opc[31:30]: 00=S(32-bit), 01=D(64-bit), 10=Q(128-bit)
        if (bits2927 == 0b011 && bits2524 == 0b00 && V == 1)
            return DecodeLdrLiteralFp(raw, address);

        // ── LDP/STP (GP): [29:27] = 101, V=0 ──
        if (bits2927 == 0b101 && V == 0)
            return DecodeLdpStp(raw, address);

        // ── LDP/STP (SIMD/FP): [29:27] = 101, V=1 ──
        // Source: il2cpp_codegen_initobj → memset → Clang STP Q0,Q1 (128-bit zero-init)
        // Source: il2cpp-codegen.h:125-128
        if (bits2927 == 0b101 && V == 1)
            return DecodeLdpStpSimd(raw, address);

        // ── LDR/STR unsigned imm (GP): [29:27] = 111, V=0, [25:24] = 01 ──
        if (bits2927 == 0b111 && V == 0 && bits2524 == 0b01)
            return DecodeLdrStrUnsignedImm(raw, address);

        // ── LDR/STR unsigned imm (SIMD/FP): [29:27] = 111, V=1, [25:24] = 01 ──
        // Source: Clang codegen for Ldc_R4/R8 → LDR Sn/Dn from .rodata constant pool
        // Source: MethodBodyWriter.cs:1271-1275
        if (bits2927 == 0b111 && V == 1 && bits2524 == 0b01)
            return DecodeLdrStrSimdUnsignedImm(raw, address);

        // ── LDR/STR pre/post (GP): [29:27] = 111, V=0, [25:24] = 00 ──
        if (bits2927 == 0b111 && V == 0 && bits2524 == 0b00)
        {
            // Check for register-offset variant: bit[21] = 1, bits[11:10] = 10
            if (((raw >> 21) & 1) == 1 && ((raw >> 10) & 3) == 0b10)
                return DecodeLdrStrRegOffset(raw, address);
            return DecodeLdrStrPrePost(raw, address);
        }

        // ── LDR/STR pre/post/reg (SIMD/FP): [29:27] = 111, V=1, [25:24] = 00 ──
        // Source: Clang emits these for unscaled FP ops and register-offset array element loads
        if (bits2927 == 0b111 && V == 1 && bits2524 == 0b00)
        {
            // Check for register-offset variant: bit[21] = 1, bits[11:10] = 10
            if (((raw >> 21) & 1) == 1 && ((raw >> 10) & 3) == 0b10)
                return DecodeLdrStrSimdRegOffset(raw, address);
            return DecodeLdrStrSimdPrePost(raw, address);
        }

        // ── AdvSIMD load/store single structure: [29:27] = 0x1, V=1 ──
        // LD1/LD2/LD3/LD4/ST1/ST2/ST3/ST4 single element — rare in IL2CPP
        if (V == 1 && (bits2927 == 0b001 || bits2927 == 0b011))
        {
            byte rt = (byte)(raw & 0x1F);
            byte rnLd = (byte)((raw >> 5) & 0x1F);
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_VECTOR_OP,
                rd: rt, rn: rnLd, is64Bit: ((raw >> 30) & 1) == 1);
        }

        return new Arm64Instruction(raw, address, Arm64Opcode.Unknown);
    }

    /// <summary>
    /// LDR (literal) — PC-relative load
    /// §C6.2.131: [31:30] opc | [29:24] 011000 | [23:5] imm19 | [4:0] Rt
    /// opc: 00=LDR(32), 01=LDR(64), 10=LDRSW, 11=PRFM
    /// offset = SignExtend(imm19, 19) << 2
    /// </summary>
    private static Arm64Instruction DecodeLdrLiteral(uint raw, ulong address)
    {
        uint opc = (raw >> 30) & 0x3;
        byte rt = (byte)(raw & 0x1F);
        uint imm19 = (raw >> 5) & 0x7FFFF;

        long offset = (long)imm19;
        // Sign-extend from 19 bits
        if ((offset & (1L << 18)) != 0)
            offset |= unchecked((long)0xFFFFFFFFFFF80000L);
        offset <<= 2;

        long target = (long)address + offset;
        bool is64 = (opc & 1) == 1;

        return new Arm64Instruction(raw, address, Arm64Opcode.LDR_LIT,
            rd: rt, immediate: target, is64Bit: is64);
    }

    /// <summary>
    /// LDR (literal, FP/SIMD) — PC-relative load into FP register
    /// DDI 0487 §C7.2.175: [31:30] opc | [29:24] 011100 | [23:5] imm19 | [4:0] Rt
    /// opc: 00=LDR St (32-bit), 01=LDR Dt (64-bit), 10=LDR Qt (128-bit)
    /// offset = SignExtend(imm19, 19) << 2
    /// 
    /// The literal pool address contains the raw IEEE754 float/double bytes.
    /// Shift field encodes SIMD size: 2=S, 3=D, 4=Q (same convention as LDR_SIMD_IMM).
    /// </summary>
    private static Arm64Instruction DecodeLdrLiteralFp(uint raw, ulong address)
    {
        uint opc = (raw >> 30) & 0x3;
        byte rt = (byte)(raw & 0x1F);
        uint imm19 = (raw >> 5) & 0x7FFFF;

        long offset = (long)imm19;
        // Sign-extend from 19 bits
        if ((offset & (1L << 18)) != 0)
            offset |= unchecked((long)0xFFFFFFFFFFF80000L);
        offset <<= 2;

        long target = (long)address + offset;

        // opc: 00=S(32-bit), 01=D(64-bit), 10=Q(128-bit)
        byte simdSize = opc switch
        {
            0 => 2, // S (32-bit)
            1 => 3, // D (64-bit)
            2 => 4, // Q (128-bit)
            _ => 2,
        };

        return new Arm64Instruction(raw, address, Arm64Opcode.LDR_LIT_FP,
            rd: rt, immediate: target, is64Bit: simdSize >= 3, shift: simdSize);
    }

    /// <summary>
    /// LDP/STP — Load/Store pair (signed offset)
    /// §C6.2.129/C6.2.268:
    /// [31:30] opc | [29:27] 101 | [26] V=0 | [25:23] xxx | [22] L | [21:15] imm7 | [14:10] Rt2 | [9:5] Rn | [4:0] Rt1
    /// L=1 → LDP, L=0 → STP
    /// offset = SignExtend(imm7, 7) << scale (scale = 2 for 32-bit, 3 for 64-bit)
    /// </summary>
    private static Arm64Instruction DecodeLdpStp(uint raw, ulong address)
    {
        uint opc = (raw >> 30) & 0x3;
        bool isLoad = ((raw >> 22) & 1) == 1;
        uint imm7 = (raw >> 15) & 0x7F;
        byte rt2 = (byte)((raw >> 10) & 0x1F);
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rt1 = (byte)(raw & 0x1F);

        // bits[25:23] determine index type:
        //   001 = post-indexed (writeback after access)
        //   010 = signed-offset (no writeback)
        //   011 = pre-indexed (writeback before access, printed with '!')
        uint indexType = (raw >> 23) & 0x7;
        // 001=post-index(wb=1), 010=signed-offset(wb=0), 011=pre-index(wb=2)
        byte writeback = indexType switch
        {
            0b011 => 2, // pre-index
            0b001 => 1, // post-index
            _ => 0      // signed offset (no writeback)
        };

        bool is64 = (opc & 0x2) != 0;
        int scale = is64 ? 3 : 2;

        long offset = (long)imm7;
        // Sign-extend from 7 bits
        if ((offset & (1L << 6)) != 0)
            offset |= unchecked((long)0xFFFFFFFFFFFFFF80L);
        offset <<= scale;

        Arm64Opcode opcode = Arm64Opcode.LDP;
        if (!isLoad) opcode = Arm64Opcode.STP;
        else if (opc == 1) opcode = Arm64Opcode.LDPSW;

        return new Arm64Instruction(raw, address,
            opcode, rd: rt1, rn: rn, rm: rt2, immediate: offset, is64Bit: is64,
            writeback: writeback);
    }

    /// <summary>
    /// LDR/STR (unsigned immediate offset)
    /// §C6.2.133/C6.2.269:
    /// [31:30] size | [29:27] 111 | [26] V=0 | [25:24] 01 | [23:22] opc | [21:10] imm12 | [9:5] Rn | [4:0] Rt
    /// size: 00=byte, 01=halfword, 10=word, 11=doubleword
    /// opc: 00=STR, 01=LDR, 10=LDRS(64bit), 11=LDRS(32bit)/PRFM
    /// offset = imm12 << size
    /// </summary>
    private static Arm64Instruction DecodeLdrStrUnsignedImm(uint raw, ulong address)
    {
        uint size = (raw >> 30) & 0x3;
        uint opc = (raw >> 22) & 0x3;
        uint imm12 = (raw >> 10) & 0xFFF;
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rt = (byte)(raw & 0x1F);

        long offset = (long)(imm12 << (int)size);

        bool isLoad = (opc & 1) == 1;
        bool is64 = size == 3;

        Arm64Opcode opcode;
        if (opc == 0b00) // STR
        {
            opcode = size switch
            {
                0 => Arm64Opcode.STRB_IMM,
                1 => Arm64Opcode.STRH_IMM,
                2 => Arm64Opcode.STR_IMM,
                3 => Arm64Opcode.STR_IMM,
                _ => Arm64Opcode.Unknown
            };
        }
        else if (opc == 0b01) // LDR
        {
            opcode = size switch
            {
                0 => Arm64Opcode.LDRB_IMM,
                1 => Arm64Opcode.LDRH_IMM,
                2 => Arm64Opcode.LDR_IMM,
                3 => Arm64Opcode.LDR_IMM,
                _ => Arm64Opcode.Unknown
            };
        }
        else if (opc == 0b10) // LDRSW (size=2), LDRSH/LDRSB for smaller
        {
            // Source: IL2CPP byte/short field access with sign-extension to 64-bit
            opcode = size switch
            {
                0 => Arm64Opcode.LDRSB_IMM,
                1 => Arm64Opcode.LDRSH_IMM,
                2 => Arm64Opcode.LDRSW_IMM,
                _ => Arm64Opcode.Unknown
            };
            is64 = true; // All opc=0b10 load into 64-bit X register
        }
        else if (opc == 0b11) // Signed loads to 32-bit: LDRSB/LDRSH
        {
            // Source: IL2CPP byte/short field access with sign-extension to 32-bit
            opcode = size switch
            {
                0 => Arm64Opcode.LDRSB_IMM,
                1 => Arm64Opcode.LDRSH_IMM,
                _ => Arm64Opcode.Unknown
            };
            is64 = false;
        }
        else
        {
            opcode = Arm64Opcode.Unknown;
        }

        // Fix is64 for explicit 64-bit loads/stores
        if (size == 3) is64 = true;
        if (size == 2 && opc <= 1) is64 = false;

        return new Arm64Instruction(raw, address, opcode,
            rd: rt, rn: rn, immediate: offset, is64Bit: is64);
    }

    /// <summary>
    /// LDR/STR (pre-indexed or post-indexed)
    /// [31:30] size | [29:27] 111 | [26] V=0 | [25:24] 00 | [23:22] opc | [20:12] imm9 | [11] = 0 | [10] = idx | [9:5] Rn | [4:0] Rt
    /// idx: 1 = pre-indexed, 0 = post-indexed (when bit[11]=0)
    /// Actually for unscaled: bit[11:10] = 00, for post: 01, for pre: 11
    /// </summary>
    private static Arm64Instruction DecodeLdrStrPrePost(uint raw, ulong address)
    {
        uint size = (raw >> 30) & 0x3;
        uint opc = (raw >> 22) & 0x3;
        uint imm9 = (raw >> 12) & 0x1FF;
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rt = (byte)(raw & 0x1F);

        // bits[11:10] determine index type:
        //   00 = unscaled (STUR/LDUR, no writeback)
        //   01 = post-indexed (writeback)
        //   11 = pre-indexed (writeback, printed with '!')
        uint idxType = (raw >> 10) & 0x3;
        // 11=pre-index(wb=2), 01=post-index(wb=1), 00=unscaled(wb=0)
        byte writeback = idxType switch
        {
            0b11 => 2, // pre-index
            0b01 => 1, // post-index
            _ => 0     // unscaled (no writeback)
        };

        long offset = (long)imm9;
        // Sign-extend from 9 bits
        if ((offset & (1L << 8)) != 0)
            offset |= unchecked((long)0xFFFFFFFFFFFFFF00L);

        bool is64 = size == 3;
        bool isLoad = (opc & 1) == 1;

        Arm64Opcode opcode;
        if (opc == 0b00) // STR
        {
            opcode = size switch
            {
                0 => Arm64Opcode.STRB_IMM,
                1 => Arm64Opcode.STRH_IMM,
                2 => Arm64Opcode.STR_IMM,
                3 => Arm64Opcode.STR_IMM,
                _ => Arm64Opcode.Unknown
            };
        }
        else if (opc == 0b01) // LDR
        {
            opcode = size switch
            {
                0 => Arm64Opcode.LDRB_IMM,
                1 => Arm64Opcode.LDRH_IMM,
                2 => Arm64Opcode.LDR_IMM,
                3 => Arm64Opcode.LDR_IMM,
                _ => Arm64Opcode.Unknown
            };
        }
        else if (opc == 0b10) // LDRS to 64-bit
        {
            opcode = size switch
            {
                0 => Arm64Opcode.LDRSB_IMM,
                1 => Arm64Opcode.LDRSH_IMM,
                2 => Arm64Opcode.LDRSW_IMM,
                _ => Arm64Opcode.Unknown
            };
            is64 = true;
        }
        else if (opc == 0b11) // LDRS to 32-bit
        {
            opcode = size switch
            {
                0 => Arm64Opcode.LDRSB_IMM,
                1 => Arm64Opcode.LDRSH_IMM,
                _ => Arm64Opcode.Unknown
            };
            is64 = false;
        }
        else
        {
            opcode = Arm64Opcode.Unknown;
        }

        // Fix is64 for explicit 64-bit loads/stores
        if (size == 3) is64 = true;
        if (size == 2 && opc <= 1) is64 = false;

        return new Arm64Instruction(raw, address, opcode,
            rd: rt, rn: rn, immediate: offset, is64Bit: is64, writeback: writeback, isUnscaled: idxType == 0);
    }

    // ================================================================
    // SIMD/FP Load/Store — unsigned immediate (V=1)
    //
    // DDI 0487 §C7.2.175/C7.2.258:
    // [31:30] size | [29:27] 111 | [26] V=1 | [25:24] 01 | [23:22] opc | [21:10] imm12 | [9:5] Rn | [4:0] Rt
    //
    // Size/opc → register width:
    //   size=0 opc=01: LDR Bt (8-bit)   scale=0
    //   size=1 opc=01: LDR Ht (16-bit)  scale=1
    //   size=2 opc=01: LDR St (32-bit)  scale=2
    //   size=3 opc=01: LDR Dt (64-bit)  scale=3
    //   size=0 opc=11: LDR Qt (128-bit) scale=4
    //   (opc=00 = STR, opc=01 = LDR)
    //
    // Source evidence: Clang emits LDR S0/D0 for float/double constant pool loads
    //   per MethodBodyWriter.cs:1271-1275 (Ldc_R4/R8 → C++ literal → Clang .rodata pool)
    // Source evidence: Clang emits STR S0/D0 for float/double field stores
    //   per MethodBodyWriter.cs:3856-3920 (stfld handler)
    // ================================================================
    private static Arm64Instruction DecodeLdrStrSimdUnsignedImm(uint raw, ulong address)
    {
        uint size = (raw >> 30) & 0x3;
        uint opc = (raw >> 22) & 0x3;
        uint imm12 = (raw >> 10) & 0xFFF;
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rt = (byte)(raw & 0x1F);

        bool isLoad = (opc & 1) == 1;

        // Determine register width and scale
        // size=0+opc[1]=1 → Q (128-bit), scale=4
        // size=0+opc[1]=0 → B (8-bit), scale=0
        // size=1 → H (16-bit), scale=1
        // size=2 → S (32-bit), scale=2
        // size=3 → D (64-bit), scale=3
        byte simdSize;
        int scale;
        if (size == 0 && (opc & 2) != 0)
        {
            simdSize = 4; // Q (128-bit)
            scale = 4;    // shift by 4 (multiply by 16)
        }
        else
        {
            simdSize = (byte)size; // 0=B, 1=H, 2=S, 3=D
            scale = (int)size;
        }

        long offset = (long)(imm12 << scale);

        return new Arm64Instruction(raw, address,
            isLoad ? Arm64Opcode.LDR_SIMD_IMM : Arm64Opcode.STR_SIMD_IMM,
            rd: rt, rn: rn, immediate: offset, is64Bit: simdSize >= 3,
            shift: simdSize);
    }

    // ================================================================
    // SIMD/FP LDP/STP (V=1)
    //
    // DDI 0487 §C7.2.174/C7.2.257:
    // [31:30] opc | [29:27] 101 | [26] V=1 | [25:23] xxx | [22] L | [21:15] imm7 | [14:10] Rt2 | [9:5] Rn | [4:0] Rt1
    // opc: 00=S(32-bit), 01=D(64-bit), 10=Q(128-bit)
    // scale: opc + 2 (2 for S, 3 for D, 4 for Q)
    //
    // Source evidence: il2cpp_codegen_initobj → memset → Clang STP Q0,Q1 for 32-byte zero-init
    //   per il2cpp-codegen.h:125-128
    // Source evidence: Binary 0xAD020660 = STP Q0, Q1, [X19, #64] (Decimal field init)
    // ================================================================
    private static Arm64Instruction DecodeLdpStpSimd(uint raw, ulong address)
    {
        uint opc = (raw >> 30) & 0x3;
        bool isLoad = ((raw >> 22) & 1) == 1;
        uint imm7 = (raw >> 15) & 0x7F;
        byte rt2 = (byte)((raw >> 10) & 0x1F);
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rt1 = (byte)(raw & 0x1F);

        // opc: 00=S, 01=D, 10=Q
        int scale = (int)opc + 2;
        byte simdSize = opc switch
        {
            0 => 2, // S (32-bit)
            1 => 3, // D (64-bit)
            2 => 4, // Q (128-bit)
            _ => 2,
        };

        long offset = (long)imm7;
        if ((offset & (1L << 6)) != 0)
            offset |= unchecked((long)0xFFFFFFFFFFFFFF80L);
        offset <<= scale;

        // bits[25:23] determine index type (same encoding as GP LDP/STP)
        uint simdIdxType = (raw >> 23) & 0x7;
        byte simdWb = simdIdxType switch
        {
            0b011 => 2, // pre-index
            0b001 => 1, // post-index
            _ => 0      // signed offset (no writeback)
        };

        return new Arm64Instruction(raw, address,
            isLoad ? Arm64Opcode.LDP_SIMD : Arm64Opcode.STP_SIMD,
            rd: rt1, rn: rn, rm: rt2, immediate: offset,
            is64Bit: simdSize >= 3, shift: simdSize, writeback: simdWb);
    }

    // ================================================================
    // SIMD/FP LDR/STR pre/post/unscaled (V=1, bits[25:24]=00)
    // ================================================================
    private static Arm64Instruction DecodeLdrStrSimdPrePost(uint raw, ulong address)
    {
        uint size = (raw >> 30) & 0x3;
        uint opc = (raw >> 22) & 0x3;
        uint imm9 = (raw >> 12) & 0x1FF;
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rt = (byte)(raw & 0x1F);

        long offset = (long)imm9;
        if ((offset & (1L << 8)) != 0)
            offset |= unchecked((long)0xFFFFFFFFFFFFFF00L);

        bool isLoad = (opc & 1) == 1;
        byte simdSize;
        if (size == 0 && (opc & 2) != 0)
            simdSize = 4; // Q
        else
            simdSize = (byte)size;

        // bits[11:10] determine index type (same encoding as GP LDR/STR pre/post)
        uint simdIdxType = (raw >> 10) & 0x3;
        byte simdWb = simdIdxType switch
        {
            0b11 => 2, // pre-index
            0b01 => 1, // post-index
            _ => 0     // unscaled (no writeback)
        };

        return new Arm64Instruction(raw, address,
            isLoad ? Arm64Opcode.LDR_SIMD_IMM : Arm64Opcode.STR_SIMD_IMM,
            rd: rt, rn: rn, immediate: offset, is64Bit: simdSize >= 3,
            shift: simdSize, writeback: simdWb, isUnscaled: simdIdxType == 0);
    }

    // ================================================================
    // LDR/STR register offset (SIMD/FP, V=1)
    //
    // DDI 0487 §C7.2.175:
    // [31:30] size | [29:27] 111 | [26] V=1 | [25:24] 00 | [23:22] opc | [21] 1
    // [20:16] Rm | [15:13] option | [12] S | [11:10] 10 | [9:5] Rn | [4:0] Rt
    //
    // Source: Clang for float/double array element access at computed indices
    //   e.g., LDR S8, [X19, X8] for arr[i] where arr is float[]
    // ================================================================
    private static Arm64Instruction DecodeLdrStrSimdRegOffset(uint raw, ulong address)
    {
        uint size = (raw >> 30) & 0x3;
        uint opc = (raw >> 22) & 0x3;
        byte rm = (byte)((raw >> 16) & 0x1F);
        uint option = (raw >> 13) & 0x7;
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rt = (byte)(raw & 0x1F);

        bool isLoad = (opc & 1) == 1;

        // Determine SIMD register width from size and opc
        byte simdSize;
        if (size == 0 && (opc & 2) != 0)
            simdSize = 4; // Q (128-bit)
        else
            simdSize = (byte)size; // 0=B, 1=H, 2=S, 3=D

        bool offsetIs64 = (option & 1) == 1;
        bool isScaled = ((raw >> 12) & 1) == 1;

        return new Arm64Instruction(raw, address,
            isLoad ? Arm64Opcode.LDR_SIMD_REG : Arm64Opcode.STR_SIMD_REG,
            rd: rt, rn: rn, rm: rm, is64Bit: simdSize >= 3,
            shift: simdSize, offsetIs64Bit: offsetIs64,
            extendOption: (byte)option, isScaled: isScaled);
    }

    // ================================================================
    // LDR/STR register offset (GP, V=0)
    //
    // DDI 0487 §C6.2.133:
    // [31:30] size | [29:27] 111 | [26] V=0 | [25:24] 00 | [23:22] opc | [21] 1
    // [20:16] Rm | [15:13] option | [12] S | [11:10] 10 | [9:5] Rn | [4:0] Rt
    // ================================================================
    private static Arm64Instruction DecodeLdrStrRegOffset(uint raw, ulong address)
    {
        uint size = (raw >> 30) & 0x3;
        uint opc = (raw >> 22) & 0x3;
        byte rm = (byte)((raw >> 16) & 0x1F);
        uint option = (raw >> 13) & 0x7;
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rt = (byte)(raw & 0x1F);

        bool is64 = size == 3;
        bool isLoad = (opc & 1) == 1;
        bool offsetIs64 = (option & 1) == 1;
        bool isScaled = ((raw >> 12) & 1) == 1;

        return new Arm64Instruction(raw, address,
            isLoad ? Arm64Opcode.LDR_REG : Arm64Opcode.STR_REG,
            rd: rt, rn: rn, rm: rm, is64Bit: is64, shift: (byte)size, offsetIs64Bit: offsetIs64,
            extendOption: (byte)option, isScaled: isScaled);
    }

    // ================================================================
    // Branches
    // §C4.1.3
    // ================================================================

    private static Arm64Instruction DecodeBranch(uint raw, ulong address)
    {
        // B/BL: [31] op | [30:26] 00101 | [25:0] imm26
        // op=0 → B, op=1 → BL
        if (((raw >> 26) & 0x1F) == 0b00101)
        {
            bool isLink = ((raw >> 31) & 1) == 1;
            uint imm26 = raw & 0x03FFFFFF;
            long offset = (long)imm26;
            // Sign-extend from 26 bits
            if ((offset & (1L << 25)) != 0)
                offset |= unchecked((long)0xFFFFFFFFFC000000L);
            offset <<= 2;
            long target = (long)address + offset;

            return new Arm64Instruction(raw, address,
                isLink ? Arm64Opcode.BL : Arm64Opcode.B,
                immediate: target, is64Bit: true);
        }

        // BR/BLR: [31:25] 1101011 | [24:21] opc | [20:16] op2 | [15:12] op3 | [11:10] op4 | [9:5] Rn | [4:0] Rm
        // BLR: 1101011 0001 11111 000000 Rn 00000
        // BR:  1101011 0000 11111 000000 Rn 00000
        if ((raw & 0xFE1FFC1F) == 0xD61F0000)
        {
            bool isLink = ((raw >> 21) & 1) == 1;
            byte rn = (byte)((raw >> 5) & 0x1F);
            return new Arm64Instruction(raw, address,
                isLink ? Arm64Opcode.BLR : Arm64Opcode.BR,
                rn: rn, is64Bit: true);
        }

        // CBZ/CBNZ: [31] sf | [30:25] 011010 | [24] op | [23:5] imm19 | [4:0] Rt
        if (((raw >> 25) & 0x3F) == 0b011010)
        {
            bool is64 = ((raw >> 31) & 1) == 1;
            bool isNZ = ((raw >> 24) & 1) == 1;
            byte rt = (byte)(raw & 0x1F);
            uint imm19 = (raw >> 5) & 0x7FFFF;
            long offset = (long)imm19;
            if ((offset & (1L << 18)) != 0)
                offset |= unchecked((long)0xFFFFFFFFFFF80000L);
            offset <<= 2;
            long target = (long)address + offset;

            return new Arm64Instruction(raw, address,
                isNZ ? Arm64Opcode.CBNZ : Arm64Opcode.CBZ,
                rd: rt, immediate: target, is64Bit: is64);
        }

        // TBZ/TBNZ: [31] b5 | [30:25] 011011 | [24] op | [23:19] b40 | [18:5] imm14 | [4:0] Rt
        if (((raw >> 25) & 0x3F) == 0b011011)
        {
            bool isNZ = ((raw >> 24) & 1) == 1;
            byte rt = (byte)(raw & 0x1F);
            uint b5 = (raw >> 31) & 1;
            uint b40 = (raw >> 19) & 0x1F;
            byte bitPos = (byte)((b5 << 5) | b40);
            uint imm14 = (raw >> 5) & 0x3FFF;
            long offset = (long)imm14;
            if ((offset & (1L << 13)) != 0)
                offset |= unchecked((long)0xFFFFFFFFFFFFC000L);
            offset <<= 2;
            long target = (long)address + offset;

            return new Arm64Instruction(raw, address,
                isNZ ? Arm64Opcode.TBNZ : Arm64Opcode.TBZ,
                rd: rt, immediate: target, is64Bit: b5 == 1, shift: bitPos);
        }

        // B.cond: [31:25] 0101010 | [24] 0 | [23:5] imm19 | [4] 0 | [3:0] cond
        if (((raw >> 24) & 0xFF) == 0b01010100)
        {
            byte cond = (byte)(raw & 0xF);
            uint imm19 = (raw >> 5) & 0x7FFFF;
            long offset = (long)imm19;
            if ((offset & (1L << 18)) != 0)
                offset |= unchecked((long)0xFFFFFFFFFFF80000L);
            offset <<= 2;
            long target = (long)address + offset;

            return new Arm64Instruction(raw, address, Arm64Opcode.B_COND,
                immediate: target, condition: cond, is64Bit: true);
        }

        return new Arm64Instruction(raw, address, Arm64Opcode.Unknown);
    }

    // ================================================================
    // Data Processing — Register
    // ================================================================

    private static Arm64Instruction DecodeDataProcessingRegister(uint raw, ulong address)
    {
        // Bits [28:24] determine the sub-group within DP-register
        uint op24 = (raw >> 24) & 0x1F;
        bool is64 = ((raw >> 31) & 1) == 1;
        byte rd = (byte)(raw & 0x1F);
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rm = (byte)((raw >> 16) & 0x1F);

        // ── Logical (shifted register): bits[28:24] = 01010 ──
        // Encoding: sf:1 opc:2 01010 shift:2 N:1 Rm:5 imm6:6 Rn:5 Rd:5
        // opc=00 → AND, opc=01 → ORR, opc=10 → EOR, opc=11 → ANDS
        // MOV alias: ORR with Rn=XZR (31), shift=0, imm6=0
        if ((op24 & 0x1F) == 0b01010)
        {
            uint opc = (raw >> 29) & 0x3;
            uint imm6 = (raw >> 10) & 0x3F;
            uint shiftType = (raw >> 22) & 3;

            int N = (int)((raw >> 21) & 1);

            // MOV alias: ORR Xd, XZR, Xm (opc=01, N=0, Rn=31, shift=0, imm6=0)
            if (opc == 0b01 && N == 0 && rn == 31 && imm6 == 0 && shiftType == 0)
            {
                return new Arm64Instruction(raw, address, Arm64Opcode.MOV_REG,
                    rd: rd, rn: rm, is64Bit: is64);
            }
            Arm64Opcode logOp = (opc, N) switch
            {
                (0b00, 0) => Arm64Opcode.AND_REG,
                (0b00, 1) => Arm64Opcode.BIC_REG,
                (0b01, 0) => Arm64Opcode.ORR_REG,
                (0b01, 1) => Arm64Opcode.ORN_REG,
                (0b10, 0) => Arm64Opcode.EOR_REG,
                (0b10, 1) => Arm64Opcode.EON_REG,
                (0b11, 0) => Arm64Opcode.ANDS_REG,
                (0b11, 1) => Arm64Opcode.BICS_REG,
                _ => Arm64Opcode.Unknown
            };

            return new Arm64Instruction(raw, address, logOp,
                rd: rd, rn: rn, rm: rm, is64Bit: is64, shift: (byte)imm6, shiftType: (byte)shiftType);
        }

        // ── Add/subtract (shifted register): bits[28:24] = 01011 ──
        // Encoding: sf:1 op:1 S:1 01011 shift:2 0 Rm:5 imm6:6 Rn:5 Rd:5
        // op=0,S=0 → ADD; op=0,S=1 → ADDS (CMN alias when Rd=31)
        // op=1,S=0 → SUB; op=1,S=1 → SUBS (CMP alias when Rd=31)
        if ((op24 & 0x1F) == 0b01011)
        {
            bool isSub = ((raw >> 30) & 1) == 1;
            bool setFlags = ((raw >> 29) & 1) == 1;

            bool isExtended = ((raw >> 21) & 1) == 1;
            byte shiftAmount;
            byte shiftType = 0;
            byte extendOption = 0;

            if (isExtended)
            {
                // Extended register (e.g. SXTW #imm3)
                // bits 12:10 hold the shift amount (0-4)
                shiftAmount = (byte)((raw >> 10) & 0x7);
                extendOption = (byte)((raw >> 13) & 0x7);
            }
            else
            {
                // Shifted register (e.g. LSL #imm6)
                shiftType = (byte)((raw >> 22) & 3);
                shiftAmount = (byte)((raw >> 10) & 0x3F);
            }

            if (isSub && setFlags)
            {
                if (rd == 31)
                {
                    // CMP alias: Rd=XZR — keep original fields, lifter handles Rd==31
                    return new Arm64Instruction(raw, address, Arm64Opcode.CMP_REG,
                        rd: rd, rn: rn, rm: rm, is64Bit: is64, shift: shiftAmount,
                        shiftType: shiftType, isExtended: isExtended, extendOption: extendOption);
                }
                return new Arm64Instruction(raw, address, Arm64Opcode.SUBS_REG,
                    rd: rd, rn: rn, rm: rm, is64Bit: is64, shift: shiftAmount,
                    shiftType: shiftType, isExtended: isExtended, extendOption: extendOption);
            }
            if (!isSub && setFlags)
            {
                return new Arm64Instruction(raw, address, Arm64Opcode.ADDS_REG,
                    rd: rd, rn: rn, rm: rm, is64Bit: is64, shift: shiftAmount,
                    shiftType: shiftType, isExtended: isExtended, extendOption: extendOption);
            }

            return new Arm64Instruction(raw, address,
                isSub ? Arm64Opcode.SUB_REG : Arm64Opcode.ADD_REG,
                rd: rd, rn: rn, rm: rm, is64Bit: is64, shift: shiftAmount,
                shiftType: shiftType, isExtended: isExtended, extendOption: extendOption);
        }

        // ── Conditional select: bits[28:21] top = 11010100 ──
        // CSEL:  sf:0 0 11010100 Rm:5 cond:4 0 0 Rn:5 Rd:5
        // CSINC: sf:0 0 11010100 Rm:5 cond:4 0 1 Rn:5 Rd:5
        // CSINV: sf:1 0 11010100 Rm:5 cond:4 0 0 Rn:5 Rd:5
        // CSNEG: sf:1 0 11010100 Rm:5 cond:4 0 1 Rn:5 Rd:5
        if (((raw >> 21) & 0xFF) == 0b11010100)
        {
            byte cond = (byte)((raw >> 12) & 0xF);
            uint op2bit = (raw >> 10) & 1;   // bit 10: 0=CSEL/CSINV, 1=CSINC/CSNEG
            uint opBit30 = (raw >> 30) & 1;  // bit 30: 0=CSEL/CSINC, 1=CSINV/CSNEG

            Arm64Opcode csOp;
            if (opBit30 == 0 && op2bit == 0) csOp = Arm64Opcode.CSEL;
            else if (opBit30 == 0 && op2bit == 1) csOp = Arm64Opcode.CSINC;
            else if (opBit30 == 1 && op2bit == 0) csOp = Arm64Opcode.CSINV;
            else csOp = Arm64Opcode.CSNEG;

            return new Arm64Instruction(raw, address, csOp,
                rd: rd, rn: rn, rm: rm, is64Bit: is64, condition: cond);
        }

        // ── Data-processing (2 source): bits[28:21] = 11010110 ──
        // SDIV:  sf:0 0 11010110 Rm 00001 0 Rn Rd
        // UDIV:  sf:0 0 11010110 Rm 00001 1 Rn Rd
        // Source: ARM ARM §C6.2.228 (SDIV), §C6.2.316 (UDIV)
        if (((raw >> 21) & 0xFF) == 0b11010110)
        {
            uint opcode2 = (raw >> 10) & 0x3F;
            if (opcode2 == 0b000010) // UDIV
                return new Arm64Instruction(raw, address, Arm64Opcode.UDIV,
                    rd: rd, rn: rn, rm: rm, is64Bit: is64);
            if (opcode2 == 0b000011) // SDIV
                return new Arm64Instruction(raw, address, Arm64Opcode.SDIV,
                    rd: rd, rn: rn, rm: rm, is64Bit: is64);
            // Variable shifts — §C6.2.153/156/14/217
            // Source: MethodBodyWriter.cs L1553: Code.Shl → C << → Clang LSLV
            if (opcode2 == 0b001000) // LSLV
                return new Arm64Instruction(raw, address, Arm64Opcode.LSLV,
                    rd: rd, rn: rn, rm: rm, is64Bit: is64);
            if (opcode2 == 0b001001) // LSRV
                return new Arm64Instruction(raw, address, Arm64Opcode.LSRV,
                    rd: rd, rn: rn, rm: rm, is64Bit: is64);
            if (opcode2 == 0b001010) // ASRV
                return new Arm64Instruction(raw, address, Arm64Opcode.ASRV,
                    rd: rd, rn: rn, rm: rm, is64Bit: is64);
            if (opcode2 == 0b001011) // RORV
                return new Arm64Instruction(raw, address, Arm64Opcode.RORV,
                    rd: rd, rn: rn, rm: rm, is64Bit: is64);
        }

        // ── Data-processing (1 source): bits[30:21] = 1011010110 ──
        // REV, REV16, RBIT, CLZ, CLS
        // Source: Clang for System.Numerics.BitOperations
        if (((raw >> 21) & 0x3FF) == 0b1011010110)
        {
            uint opcode1 = (raw >> 10) & 0x3F;
            // All DP-1-source ops produce a GP result; track as generic
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_VECTOR_OP,
                rd: rd, rn: rn, is64Bit: is64);
        }

        // ── Add/Subtract with carry: bits[28:21] = 11010000 ──
        // ADC/ADCS/SBC/SBCS — §C6.2.2/C6.2.224
        if (((raw >> 21) & 0xFF) == 0b11010000)
        {
            bool isSub = ((raw >> 30) & 1) == 1;
            return new Arm64Instruction(raw, address,
                isSub ? Arm64Opcode.SUB_REG : Arm64Opcode.ADD_REG,
                rd: rd, rn: rn, rm: rm, is64Bit: is64);
        }

        // ── Data-processing (3 source): bits[28:24] = 11011 ──
        // Sub-groups: bits[23:21] select between MADD/MSUB/SMADDL/SMSUBL/SMULH/UMADDL/UMSUBL/UMULH
        // Source: ARM ARM §C6.2.160 (MADD), §C6.2.162 (MSUB), §C6.2.229 (SMULL), §C6.2.310 (UMULL)
        if ((op24 & 0x1F) == 0b11011)
        {
            uint op31 = (raw >> 21) & 7;   // bits[23:21]
            bool o0 = ((raw >> 15) & 1) == 1; // bit[15]
            byte ra = (byte)((raw >> 10) & 0x1F);

            // op31=000: MADD/MSUB (32-bit if sf=0, 64-bit if sf=1)
            if (op31 == 0b000)
            {
                return new Arm64Instruction(raw, address,
                    o0 ? Arm64Opcode.MSUB : Arm64Opcode.MADD,
                    rd: rd, rn: rn, rm: rm, is64Bit: is64, shift: ra);
            }
            // op31=001: SMADDL/SMSUBL (always 64-bit result from 32-bit sources)
            // SMULL alias when Ra=XZR
            if (op31 == 0b001)
            {
                return new Arm64Instruction(raw, address,
                    o0 ? Arm64Opcode.MSUB : Arm64Opcode.SMULL,
                    rd: rd, rn: rn, rm: rm, is64Bit: true, shift: ra);
            }
            // op31=010: SMULH (always 64×64→64 high bits)
            if (op31 == 0b010)
            {
                return new Arm64Instruction(raw, address, Arm64Opcode.SMULH,
                    rd: rd, rn: rn, rm: rm, is64Bit: true);
            }
            // op31=101: UMADDL/UMSUBL
            // UMULL alias when Ra=XZR
            if (op31 == 0b101)
            {
                return new Arm64Instruction(raw, address,
                    o0 ? Arm64Opcode.MSUB : Arm64Opcode.UMULL,
                    rd: rd, rn: rn, rm: rm, is64Bit: true, shift: ra);
            }
            // op31=110: UMULH
            if (op31 == 0b110)
            {
                return new Arm64Instruction(raw, address, Arm64Opcode.UMULH,
                    rd: rd, rn: rn, rm: rm, is64Bit: true);
            }
        }

        // ── Conditional compare (register): bits[28:21] = 11010010 ──
        // CCMP:  sf 1 1 11010010 Rm cond 10 Rn 0 nzcv
        // CCMN:  sf 0 1 11010010 Rm cond 10 Rn 0 nzcv
        // Source: Clang for complex if(a && b) chains
        if (((raw >> 21) & 0xFF) == 0b11010010 && ((raw >> 11) & 1) == 0)
        {
            byte cond = (byte)((raw >> 12) & 0xF);
            byte nzcv = (byte)(raw & 0xF);
            bool isCCMP = ((raw >> 30) & 1) == 1; // op=1→CCMP, op=0→CCMN

            return new Arm64Instruction(raw, address,
                isCCMP ? Arm64Opcode.CCMP : Arm64Opcode.CCMN,
                rn: rn, rm: rm, immediate: nzcv, is64Bit: is64, condition: cond);
        }

        // ── Conditional compare (immediate): bits[28:21] = 11010010, bit[11]=1 ──
        // CCMP imm: sf 1 1 11010010 imm5 cond 10 Rn 0 nzcv — but bit encoding differs
        // Actually: sf op S 11010010 imm5 cond 10 Rn 0 nzcv → bit[11] discriminates
        // The immediate form has bits[25:24]=10 and bit[11]=1
        if (((raw >> 21) & 0xFF) == 0b11010010 && ((raw >> 11) & 1) == 1)
        {
            byte cond = (byte)((raw >> 12) & 0xF);
            byte nzcv = (byte)(raw & 0xF);
            byte imm5 = (byte)((raw >> 16) & 0x1F);
            bool isCCMP = ((raw >> 30) & 1) == 1;

            return new Arm64Instruction(raw, address,
                isCCMP ? Arm64Opcode.CCMP_IMM : Arm64Opcode.CCMN_IMM,
                rn: rn, rm: imm5, immediate: nzcv, is64Bit: is64, condition: cond);
        }

        return new Arm64Instruction(raw, address, Arm64Opcode.Unknown);
    }

    // ================================================================
    // SIMD / Floating-Point Data Processing
    // §C7.2 — Floating-point instructions
    // All verified via Capstone against actual libil2cpp.so binary.
    // ================================================================

    private static Arm64Instruction DecodeSIMDFP(uint raw, ulong address)
    {
        byte rd = (byte)(raw & 0x1F);
        byte rn = (byte)((raw >> 5) & 0x1F);
        byte rm = (byte)((raw >> 16) & 0x1F);
        uint ftype = (raw >> 22) & 3;   // 00=single, 01=double
        bool isDouble = ftype == 1;
        byte precSize = isDouble ? (byte)3 : (byte)2;  // For Shift field: 2=S, 3=D

        // ── MOVI vector zero: 0x6F00E400 (Q=1) or 0x2F00E400 (Q=0) ──
        // AdvSIMD modified immediate: op=1, cmode=1110, o2=0
        // Source: Clang for initobj / memset(0) / struct zero-init
        if ((raw & 0xBFFFFFE0) == 0x2F00E400)
        {
            bool Q = ((raw >> 30) & 1) == 1;
            return new Arm64Instruction(raw, address, Arm64Opcode.MOVI_ZERO,
                rd: rd, is64Bit: Q);
        }

        // ── FP data-processing 1-source ──
        // Encoding: M:0:S:11110:ftype:1:0000:opcode[5:0]:10000:Rn:Rd
        // Mask: bits[14:10] must be 10000 = 0x10
        if ((raw & 0x5F207C00) == 0x1E204000)
        {
            uint fpOpcode = (raw >> 15) & 0x3F;
            switch (fpOpcode)
            {
                case 0b000000: // FMOV (register)
                    return new Arm64Instruction(raw, address, Arm64Opcode.FMOV_FP_REG,
                        rd: rd, rn: rn, is64Bit: isDouble, shift: precSize);
                case 0b000001: // FABS
                    return new Arm64Instruction(raw, address, Arm64Opcode.FABS,
                        rd: rd, rn: rn, is64Bit: isDouble, shift: precSize);
                case 0b000010: // FNEG
                    return new Arm64Instruction(raw, address, Arm64Opcode.FNEG,
                        rd: rd, rn: rn, is64Bit: isDouble, shift: precSize);
                case 0b000011: // FSQRT
                    return new Arm64Instruction(raw, address, Arm64Opcode.FSQRT,
                        rd: rd, rn: rn, is64Bit: isDouble, shift: precSize);
                case 0b000100: // FCVT — between precisions
                case 0b000101:
                    return new Arm64Instruction(raw, address, Arm64Opcode.FCVT_PREC,
                        rd: rd, rn: rn, is64Bit: ftype == 0, shift: precSize);
                // FP rounding — §C7.2.77-82
                // Source: il2cpp-codegen.h L1498: bankers_roundf → FRINTN
                case 0b001000: // FRINTN — round to nearest, ties to even
                    return new Arm64Instruction(raw, address, Arm64Opcode.FRINTN,
                        rd: rd, rn: rn, is64Bit: isDouble, shift: precSize);
                case 0b001001: // FRINTP — round toward +∞
                    return new Arm64Instruction(raw, address, Arm64Opcode.FRINTP,
                        rd: rd, rn: rn, is64Bit: isDouble, shift: precSize);
                case 0b001010: // FRINTM — round toward -∞
                    return new Arm64Instruction(raw, address, Arm64Opcode.FRINTM,
                        rd: rd, rn: rn, is64Bit: isDouble, shift: precSize);
                case 0b001011: // FRINTZ — round toward zero
                    return new Arm64Instruction(raw, address, Arm64Opcode.FRINTZ,
                        rd: rd, rn: rn, is64Bit: isDouble, shift: precSize);
                case 0b001100: // FRINTA — round to nearest, ties away
                    return new Arm64Instruction(raw, address, Arm64Opcode.FRINTA,
                        rd: rd, rn: rn, is64Bit: isDouble, shift: precSize);
                case 0b001110: // FRINTX — round to integral exact
                    return new Arm64Instruction(raw, address, Arm64Opcode.FRINTX,
                        rd: rd, rn: rn, is64Bit: isDouble, shift: precSize);
            }
        }

        // ── FP compare ──
        // FCMP: M:0:S:11110:ftype:1:Rm:00:1000:Rn:0x:000
        // Mask: (raw & 0xFF20FC07) == 0x1E202000
        if ((raw & 0xFF20FC07) == 0x1E202000)
        {
            // bit[3] = opc → 0=FCMP, 1=FCMPE
            // bit[4:3] = 00 → compare to register, 01 → compare to zero
            byte cmpRm = ((raw >> 3) & 1) == 1 ? (byte)0xFF : rm; // 0xFF = zero sentinel
            return new Arm64Instruction(raw, address, Arm64Opcode.FCMP,
                rn: rn, rm: cmpRm, is64Bit: isDouble, shift: precSize);
        }

        // ── FP data-processing 2-source ──
        // Encoding: M:0:S:11110:ftype:1:Rm:opcode[3:0]:10:Rn:Rd
        // Mask: (raw & 0x5F200C00) == 0x1E200800
        if ((raw & 0x5F200C00) == 0x1E200800)
        {
            uint fpOp2 = (raw >> 12) & 0xF;
            Arm64Opcode fpArith = fpOp2 switch
            {
                0b0000 => Arm64Opcode.FMUL,
                0b0001 => Arm64Opcode.FDIV,
                0b0010 => Arm64Opcode.FADD,
                0b0011 => Arm64Opcode.FSUB,
                0b0100 => Arm64Opcode.FMAX,
                0b0101 => Arm64Opcode.FMIN,
                0b0110 => Arm64Opcode.FMAXNM,
                0b0111 => Arm64Opcode.FMINNM,
                0b1000 => Arm64Opcode.FNMUL,
                _ => Arm64Opcode.Unknown
            };
            if (fpArith != Arm64Opcode.Unknown)
            {
                return new Arm64Instruction(raw, address, fpArith,
                    rd: rd, rn: rn, rm: rm, is64Bit: isDouble, shift: precSize);
            }
        }

        // ── FP conditional select (FCSEL) ──
        // Encoding: M:0:S:11110:ftype:1:Rm:cond:11:Rn:Rd
        // Mask: (raw & 0x5F200C00) == 0x1E200C00
        if ((raw & 0x5F200C00) == 0x1E200C00)
        {
            byte cond = (byte)((raw >> 12) & 0xF);
            return new Arm64Instruction(raw, address, Arm64Opcode.FCSEL,
                rd: rd, rn: rn, rm: rm, is64Bit: isDouble, shift: precSize, condition: cond);
        }

        // ── FP ↔ integer conversion ──
        // Encoding: sf:0:S:11110:ftype:1:rmode:opcode:000000:Rn:Rd
        // Mask: (raw & 0x5F20FC00) == 0x1E200000
        if ((raw & 0x5F20FC00) == 0x1E200000)
        {
            uint rmode = (raw >> 19) & 3;
            uint opcode = (raw >> 16) & 7;
            bool sf = ((raw >> 31) & 1) == 1;

            uint key = (rmode << 3) | opcode;
            switch (key)
            {
                case 0b11_000: // FCVTZS
                    return new Arm64Instruction(raw, address, Arm64Opcode.FCVTZS,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
                case 0b11_001: // FCVTZU
                    return new Arm64Instruction(raw, address, Arm64Opcode.FCVTZU,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
                case 0b00_010: // SCVTF
                    return new Arm64Instruction(raw, address, Arm64Opcode.SCVTF,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
                case 0b00_011: // UCVTF
                    return new Arm64Instruction(raw, address, Arm64Opcode.UCVTF,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
                case 0b00_110: // FMOV GP→FP (Wd→Sn or Xd→Dn)
                    return new Arm64Instruction(raw, address, Arm64Opcode.FMOV_FP_TO_GP,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
                case 0b00_111: // FMOV FP→GP (Sn→Wd or Dn→Xd)
                    return new Arm64Instruction(raw, address, Arm64Opcode.FMOV_GP_TO_FP,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
                // FP conversion with rounding modes — §C7.2.73-76
                case 0b00_000: // FCVTNS
                    return new Arm64Instruction(raw, address, Arm64Opcode.FCVTNS,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
                case 0b00_001: // FCVTNU
                    return new Arm64Instruction(raw, address, Arm64Opcode.FCVTNU,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
                case 0b01_000: // FCVTPS
                    return new Arm64Instruction(raw, address, Arm64Opcode.FCVTPS,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
                case 0b01_001: // FCVTPU
                    return new Arm64Instruction(raw, address, Arm64Opcode.FCVTPU,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
                case 0b10_000: // FCVTMS
                    return new Arm64Instruction(raw, address, Arm64Opcode.FCVTMS,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
                case 0b10_001: // FCVTMU
                    return new Arm64Instruction(raw, address, Arm64Opcode.FCVTMU,
                        rd: rd, rn: rn, is64Bit: sf, shift: precSize);
            }
        }

        // ── FP ↔ integer conversion (fixed-point) ──
        // Encoding: sf:0:0:11110:ftype:00:rmode:opcode:scale:Rn:Rd
        // Mask: (raw & 0x5F300000) == 0x1E000000
        if ((raw & 0x5F300000) == 0x1E000000)
        {
            uint rmode = (raw >> 18) & 3;
            uint opcode = (raw >> 16) & 3;
            bool sf = ((raw >> 31) & 1) == 1;
            byte fbits = (byte)(64 - ((raw >> 10) & 0x3F));
            
            uint key = (rmode << 2) | opcode;
            switch (key)
            {
                case 0b00_10: // SCVTF
                    return new Arm64Instruction(raw, address, Arm64Opcode.SCVTF, rd: rd, rn: rn, immediate: fbits, is64Bit: sf, shift: precSize);
                case 0b00_11: // UCVTF
                    return new Arm64Instruction(raw, address, Arm64Opcode.UCVTF, rd: rd, rn: rn, immediate: fbits, is64Bit: sf, shift: precSize);
                case 0b11_00: // FCVTZS
                    return new Arm64Instruction(raw, address, Arm64Opcode.FCVTZS, rd: rd, rn: rn, immediate: fbits, is64Bit: sf, shift: precSize);
                case 0b11_01: // FCVTZU
                    return new Arm64Instruction(raw, address, Arm64Opcode.FCVTZU, rd: rd, rn: rn, immediate: fbits, is64Bit: sf, shift: precSize);
            }
        }

        // ── FP immediate (FMOV Sn, #const / FMOV Dn, #const) ──
        // Encoding: M:0:S:11110:ftype:1:imm8:100:00000:Rd
        // Mask: (raw & 0x5F201C00) == 0x1E201000
        if ((raw & 0x5F201C00) == 0x1E201000)
        {
            byte imm8 = (byte)((raw >> 13) & 0xFF);
            return new Arm64Instruction(raw, address, Arm64Opcode.FMOV_FP_CONST,
                rd: rd, immediate: imm8, is64Bit: isDouble, shift: precSize);
        }

        // ── FP conditional compare (FCCMP/FCCMPE) ──
        if ((raw & 0x5F200C00) == 0x1E200400)
        {
            byte cond = (byte)((raw >> 12) & 0xF);
            byte nzcv = (byte)(raw & 0xF);
            return new Arm64Instruction(raw, address, Arm64Opcode.FCCMP,
                rn: rn, rm: rm, condition: cond, immediate: nzcv, is64Bit: isDouble, shift: precSize);
        }

        // ── AdvSIMD three same: [31]=0 [30]=Q [29]=U [28:24]=01110 [23:22]=size [21]=1 [20:16]=Rm [15:11]=opcode [10]=1 [9:5]=Rn [4:0]=Rd ──
        // Source: Clang for Vector2/3/4 math (FADD, FMUL, ADD, SUB, AND, ORR etc.)
        if (((raw >> 24) & 0x1F) == 0b01110 && ((raw >> 21) & 1) == 1 && ((raw >> 10) & 1) == 1)
        {
            bool Q = ((raw >> 30) & 1) == 1;
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_VECTOR_OP,
                rd: rd, rn: rn, rm: rm, is64Bit: Q);
        }

        // ── AdvSIMD scalar three same: [31:30]=01 [29]=U [28:24]=11110 [21]=1 [10]=1 ──
        // Source: Clang for scalar FP pairwise ops (FADDP scalar, FRECPS etc.)
        if (((raw >> 24) & 0x1F) == 0b11110 && ((raw >> 21) & 1) == 1 && ((raw >> 10) & 1) == 1)
        {
            uint scalarOp = (raw >> 11) & 0x1F;
            if (scalarOp == 0b11011) // FADDP (scalar pairwise)
            {
                bool Q = ((raw >> 30) & 1) == 1;
                return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_FADDP,
                    rd: rd, rn: rn, is64Bit: Q);
            }
            // Other scalar three-same ops → generic vector
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_VECTOR_OP,
                rd: rd, rn: rn, rm: rm, is64Bit: ((raw >> 30) & 1) == 1);
        }

        // ── AdvSIMD copy: [31]=0 [30]=Q [29]=op [28:21]=01110:000 → DUP/INS/UMOV/SMOV ──
        // Source: Clang for vector element access (v.x, v.y, v.z, v.w)
        if (((raw >> 24) & 0x1F) == 0b01110 && ((raw >> 21) & 7) == 0 && ((raw >> 10) & 1) == 1)
        {
            uint imm4 = (raw >> 11) & 0xF;
            uint op = (raw >> 29) & 1;
            bool Q = ((raw >> 30) & 1) == 1;
            byte imm5 = (byte)((raw >> 16) & 0x1F);

            if (imm4 == 0b0000 && op == 0) // DUP (element)
                return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_DUP, rd: rd, rn: rn, shift: imm5, is64Bit: Q);
            if (imm4 == 0b0001 && op == 0) // DUP (general)
                return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_DUP_GP, rd: rd, rn: rn, shift: imm5, is64Bit: Q);
            if (op == 1) // INS (element)
                return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_INS, rd: rd, rn: rn, shift: imm5, is64Bit: Q);
            if (imm4 == 0b0011 && op == 0) // INS (general)
                return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_INS, rd: rd, rn: rn, shift: imm5, is64Bit: Q);
            if (imm4 == 0b0111 && op == 0) // UMOV
                return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_UMOV, rd: rd, rn: rn, shift: imm5, is64Bit: Q);
            if (imm4 == 0b0101 && op == 0) // SMOV
                return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_UMOV, rd: rd, rn: rn, shift: imm5, is64Bit: Q);
            
            // Catch-all
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_UMOV, rd: rd, rn: rn, shift: imm5, is64Bit: Q);
        }

        // ── AdvSIMD EXT: [31]=0 [30]=Q [29:21]=101110:xx:0 [15]=0 [10]=0 ──
        // Encoding: 0:Q:101110:op2:0:Rm:0:imm4:0:Rn:Rd
        // Source: Clang for vector shuffle/permute
        if (((raw >> 24) & 0x5F) == 0x2E && ((raw >> 15) & 1) == 0 && ((raw >> 10) & 1) == 0
            && ((raw >> 21) & 1) == 0)
        {
            byte imm4 = (byte)((raw >> 11) & 0xF);
            bool Q = ((raw >> 30) & 1) == 1;
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_EXT,
                rd: rd, rn: rn, rm: rm, shift: imm4, is64Bit: Q);
        }

        // ── AdvSIMD shift by immediate: [31]=0 [30]=Q [29]=U [28:23]=0F:xxx (varies) ──
        // Source: Clang for vector shift operations
        if (((raw >> 23) & 0x3F) == 0b011110 || ((raw >> 23) & 0x3F) == 0b001111)
        {
            byte immh = (byte)((raw >> 19) & 0xF);
            if (immh != 0)
            {
                uint opcode5 = (raw >> 11) & 0x1F;
                bool Q = ((raw >> 30) & 1) == 1;
                byte immh_immb = (byte)((raw >> 16) & 0x7F); // immh:immb
                if (opcode5 == 0b01010) // SHL
                    return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_SHL,
                        rd: rd, rn: rn, shift: immh_immb, is64Bit: Q);
                if (opcode5 == 0b00000) // SSHR/USHR
                {
                    bool U = ((raw >> 29) & 1) == 1;
                    return new Arm64Instruction(raw, address,
                        U ? Arm64Opcode.SIMD_USHR : Arm64Opcode.SIMD_SSHR,
                        rd: rd, rn: rn, shift: immh_immb, is64Bit: Q);
                }
                if (opcode5 == 0b10100) // SSHLL/USHLL
                {
                    bool U = ((raw >> 29) & 1) == 1;
                    return new Arm64Instruction(raw, address,
                        U ? Arm64Opcode.SIMD_USHLL : Arm64Opcode.SIMD_SSHLL,
                        rd: rd, rn: rn, shift: immh_immb, is64Bit: Q);
                }
                // Other shift-by-imm ops → generic
                return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_VECTOR_OP,
                    rd: rd, rn: rn, is64Bit: Q);
            }
        }

        // ── AdvSIMD modified immediate (non-zero): [31]=0 [30]=Q [29]=op [28:24]=01111, [23:19]=00000 ──
        // Catches non-zero MOVI variants not caught by the zero check above
        if (((raw >> 24) & 0x1F) == 0b01111 && ((raw >> 19) & 0x1F) == 0b00000)
        {
            bool Q = ((raw >> 30) & 1) == 1;
            long imm = ((raw >> 5) & 0x1F) | (((raw >> 16) & 7) << 5);
            byte cmode = (byte)((raw >> 12) & 0xF);
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_MOVI,
                rd: rd, immediate: imm, shift: cmode, is64Bit: Q);
        }

        // ── AdvSIMD (scalar and vector) x indexed element: [28:24]=01111 or 11111, bit[10]=0 ──
        // Source: Clang for FMLA/FMUL/MLA/MUL by element (dot products, matrix math)
        // Decoded by SimdMnemonicResolver.DecodeIndexedElement
        if ((((raw >> 24) & 0x1F) == 0b01111 || ((raw >> 24) & 0x1F) == 0b11111) && ((raw >> 10) & 1) == 0)
        {
            bool Q = ((raw >> 30) & 1) == 1;
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_VECTOR_OP,
                rd: rd, rn: rn, rm: rm, is64Bit: Q);
        }

        // ── AdvSIMD two-reg misc: catches remaining unclassified SIMD ──
        if (((raw >> 24) & 0x1F) == 0b01110 && ((raw >> 17) & 0x1F) == 0b10000
            && ((raw >> 10) & 3) == 0b10)
        {
            bool Q = ((raw >> 30) & 1) == 1;
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_VECTOR_OP,
                rd: rd, rn: rn, is64Bit: Q);
        }

        // ── AdvSIMD three different: [28:24]=01110 [21]=1 [10]=0 ──
        // Source: Clang for widening/narrowing vector ops (SADDL, UMLAL, etc.)
        if (((raw >> 24) & 0x1F) == 0b01110 && ((raw >> 21) & 1) == 1 && ((raw >> 10) & 1) == 0)
        {
            bool Q = ((raw >> 30) & 1) == 1;
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_VECTOR_OP,
                rd: rd, rn: rn, rm: rm, is64Bit: Q);
        }

        // ── AdvSIMD scalar copy: [28:24]=11110 [21]=0 [10]=1 ──
        // DUP (scalar from element) — MOV alias: e.g. MOV S1, V0.S[1]
        if (((raw >> 24) & 0x1F) == 0b11110 && ((raw >> 21) & 1) == 0 && ((raw >> 10) & 1) == 1)
        {
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_DUP_ELEMENT,
                rd: rd, rn: rn, shift: (byte)((raw >> 16) & 0x1F), is64Bit: false);
        }

        // ── AdvSIMD scalar two-reg misc / scalar pairwise: [28:24]=11110 [21]=1 [10]=0 ──
        // FRECPS, FRSQRTS, scalar FADD pairwise etc.
        if (((raw >> 24) & 0x1F) == 0b11110 && ((raw >> 21) & 1) == 1 && ((raw >> 10) & 1) == 0)
        {
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_VECTOR_OP,
                rd: rd, rn: rn, rm: rm, is64Bit: ((raw >> 30) & 1) == 1);
        }

        // ── FP data-processing 3-source: [28:24]=11111 ──
        // FMADD, FMSUB, FNMADD, FNMSUB
        // Encoding: [31]=M, [23:22]=type, [21]=o1, [15]=o0
        //   o1=0,o0=0 → FMADD (Rd = Ra + Rn*Rm)
        //   o1=0,o0=1 → FMSUB (Rd = Ra - Rn*Rm)
        //   o1=1,o0=0 → FNMADD (Rd = -(Ra + Rn*Rm))
        //   o1=1,o0=1 → FNMSUB (Rd = -(Ra - Rn*Rm))
        // Ra is in bits [14:10], stored in Shift field (same as MADD)
        if (((raw >> 24) & 0x1F) == 0b11111 && ((raw >> 29) & 0x3) == 0)
        {
            byte ra = (byte)((raw >> 10) & 0x1F);
            int o1 = (int)((raw >> 21) & 1);
            int o0 = (int)((raw >> 15) & 1);
            var fmaOpcode = (o1, o0) switch
            {
                (0, 0) => Arm64Opcode.FMADD,
                (0, 1) => Arm64Opcode.FMSUB,
                (1, 0) => Arm64Opcode.FNMADD,
                (1, 1) => Arm64Opcode.FNMSUB,
                _ => Arm64Opcode.FMADD, // unreachable
            };
            return new Arm64Instruction(raw, address, fmaOpcode,
                rd: rd, rn: rn, rm: rm, shift: ra, is64Bit: isDouble);
        }

        // ── AdvSIMD load/store single structure: [28:24]=01101 ──
        // LD1/ST1 single element
        if (((raw >> 24) & 0x1F) == 0b01101)
        {
            bool Q = ((raw >> 30) & 1) == 1;
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_VECTOR_OP,
                rd: rd, rn: rn, is64Bit: Q);
        }

        // ── AdvSIMD scalar three-same with [10]=0 (across/pairwise) ──
        if (((raw >> 24) & 0x1F) == 0b11110)
        {
            return new Arm64Instruction(raw, address, Arm64Opcode.SIMD_VECTOR_OP,
                rd: rd, rn: rn, rm: rm, is64Bit: ((raw >> 30) & 1) == 1);
        }

        return new Arm64Instruction(raw, address, Arm64Opcode.Unknown);
    }
}
