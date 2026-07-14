namespace Rosetta.Lifter.IR.Nodes;

/// <summary>
/// A single operand in an IR instruction.
/// Operands are value types for zero-allocation IR construction.
///
/// Design: Each operand is tagged by <see cref="IrOperandKind"/> and carries
/// a union of possible values. Only the fields relevant to the kind are valid.
///
/// Scalability: When connecting to CFG, Register operands become SSA variables,
/// Immediate operands become constant nodes, and Label operands become CFG edges.
/// </summary>
public readonly struct IrOperand
{
    /// <summary>What kind of operand this is.</summary>
    public readonly IrOperandKind Kind;

    /// <summary>
    /// For Register: register number (0-30 = GP, 100+ = FP/SIMD).
    /// For Label: target address.
    /// For Immediate: the immediate value.
    /// For Memory: the base register.
    /// </summary>
    public readonly long Value;

    /// <summary>
    /// For Memory: the offset from base.
    /// For Register: unused (0).
    /// For BitWidth: the width in bits (8, 16, 32, 64).
    /// </summary>
    public readonly long Offset;

    /// <summary>
    /// Width of the operand in bits (8, 16, 32, 64, 128).
    /// Critical for type inference in later passes.
    /// </summary>
    public readonly byte BitWidth;

    /// <summary>
    /// Human-readable name/annotation (e.g., resolved method name, string literal, type name).
    /// Null for unresolved operands.
    /// </summary>
    public readonly string? Name;

    /// <summary>
    /// For SIMD registers: the width of a single element (e.g., 32 for float).
    /// </summary>
    public readonly byte ElementWidth;

    /// <summary>
    /// For SIMD registers: the number of elements in the vector (e.g., 2, 4).
    /// </summary>
    public readonly byte ElementCount;

    private IrOperand(IrOperandKind kind, long value, long offset, byte bitWidth, string? name, byte elementWidth = 0, byte elementCount = 0)
    {
        Kind = kind;
        Value = value;
        Offset = offset;
        BitWidth = bitWidth;
        Name = name;
        ElementWidth = elementWidth;
        ElementCount = elementCount;
    }

    // ─── Factory Methods ────────────────────────────────────────────────────

    /// <summary>Create a GP register operand (X0-X30 or W0-W30).</summary>
    public static IrOperand Register(int regNum, bool is64Bit)
        => new(IrOperandKind.Register, regNum, 0, (byte)(is64Bit ? 64 : 32), null);

    /// <summary>Create a named register operand (for virtual/temp registers).</summary>
    public static IrOperand NamedRegister(int regNum, byte bitWidth, string name)
        => new(IrOperandKind.Register, regNum, 0, bitWidth, name);

    /// <summary>Create a FP/SIMD register operand (S0-S31, D0-D31, Q0-Q31).</summary>
    public static IrOperand FpRegister(int regNum, byte bitWidth)
        => new(IrOperandKind.FpRegister, regNum, 0, bitWidth, null);

    /// <summary>Create a structured SIMD vector register operand (e.g. v13.2s, v13.4s).</summary>
    public static IrOperand VectorRegister(int regNum, byte elementWidth, byte elementCount)
        => new(IrOperandKind.FpRegister, regNum, 0, (byte)(elementWidth * elementCount), null, elementWidth, elementCount);

    /// <summary>Create an integer immediate operand.</summary>
    public static IrOperand Immediate(long value, byte bitWidth = 64)
        => new(IrOperandKind.Immediate, value, 0, bitWidth, null);

    /// <summary>Create a float immediate operand (encoded as raw bits).</summary>
    public static IrOperand FloatImmediate(long rawBits, byte bitWidth)
        => new(IrOperandKind.FloatImmediate, rawBits, 0, bitWidth, null);

    /// <summary>Create a 128-bit SIMD immediate operand.</summary>
    public static IrOperand SimdImmediate(long rawLo, long rawHi)
        => new(IrOperandKind.SimdImmediate, rawLo, rawHi, 128, null);

    /// <summary>Create a memory operand [base + offset].</summary>
    public static IrOperand Memory(int baseReg, long offset, byte accessWidth)
        => new(IrOperandKind.Memory, baseReg, offset, accessWidth, null);

    /// <summary>Create a memory operand with annotation [base + offset] ; name.</summary>
    public static IrOperand AnnotatedMemory(int baseReg, long offset, byte accessWidth, string annotation)
        => new(IrOperandKind.Memory, baseReg, offset, accessWidth, annotation);

    /// <summary>Create a branch target label (absolute VA).</summary>
    public static IrOperand Label(ulong targetAddress)
        => new(IrOperandKind.Label, (long)targetAddress, 0, 64, null);

    /// <summary>Create a named call target (resolved method/helper name).</summary>
    public static IrOperand CallTarget(ulong targetAddress, string name)
        => new(IrOperandKind.Label, (long)targetAddress, 0, 64, name);

    /// <summary>Create a condition code operand.</summary>
    public static IrOperand Condition(byte condCode)
        => new(IrOperandKind.Condition, condCode, 0, 4, ConditionName(condCode));

    /// <summary>The zero register (XZR/WZR) — represents constant 0.</summary>
    public static IrOperand Zero(bool is64Bit)
        => new(IrOperandKind.Immediate, 0, 0, (byte)(is64Bit ? 64 : 32), null);

    /// <summary>The stack pointer register.</summary>
    public static IrOperand StackPointer()
        => new(IrOperandKind.Register, 31, 0, 64, "SP");

    // ─── Display ────────────────────────────────────────────────────────────

    public override string ToString() => Kind switch
    {
        IrOperandKind.Register => Name ?? (Value == 31 ? "SP" : $"{(BitWidth > 32 ? "x" : "w")}{Value}"),
        IrOperandKind.FpRegister => Name ?? (ElementCount > 1 
            ? $"v{Value}.{ElementCount}{FpPrefix(ElementWidth)}" 
            : $"{FpPrefix(BitWidth)}{Value}"),
        IrOperandKind.Immediate => FormatImmediate(Value, BitWidth),
        IrOperandKind.FloatImmediate => BitWidth switch
        {
            // Half-precision (16-bit): different exponent bias & mantissa than float32.
            // Must use proper Half conversion, not raw cast to Single.
            16 => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:G}f", (float)BitConverter.UInt16BitsToHalf((ushort)Value)),
            // Single-precision (32-bit)
            <= 32 => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:G}f", BitConverter.Int32BitsToSingle((int)Value)),
            // Double-precision (64-bit)
            _ => string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:G}d", BitConverter.Int64BitsToDouble(Value)),
        },
        IrOperandKind.SimdImmediate => $"simd_128(0x{(ulong)Offset:X16}, 0x{(ulong)Value:X16})",
        IrOperandKind.Memory => Name != null
            ? $"[{RegName((int)Value)} {FmtOffset(Offset)}]  ; {Name}"
            : Offset == 0
                ? $"[{RegName((int)Value)}]"
                : $"[{RegName((int)Value)} {FmtOffset(Offset)}]",
        IrOperandKind.Label => Name != null
            ? $"{Name}"
            : $"0x{(ulong)Value:X}",
        IrOperandKind.Condition => Name ?? $"cond_{Value}",
        _ => "?"
    };

    private static string FpPrefix(byte width) => width switch
    {
        8 => "b", 16 => "h", 32 => "s", 64 => "d", 128 => "q", _ => "v"
    };

    private static string RegName(int reg) => reg == 31 ? "SP" : $"x{reg}";

    private static string FormatImmediate(long val, byte bitWidth)
    {
        if (val >= -16 && val <= 16) return val.ToString();
        if (val < 0) return $"-0x{-val:X}";
        return $"0x{val:X}";
    }

    private static string FmtOffset(long offset)
    {
        if (offset < 0) return $"- 0x{-offset:X}";
        return $"+ 0x{offset:X}";
    }

    private static string ConditionName(byte cond) => (cond & 0xF) switch
    {
        0x0 => "eq", 0x1 => "ne", 0x2 => "hs", 0x3 => "lo",
        0x4 => "mi", 0x5 => "pl", 0x6 => "vs", 0x7 => "vc",
        0x8 => "hi", 0x9 => "ls", 0xA => "ge", 0xB => "lt",
        0xC => "gt", 0xD => "le", 0xE => "al", _ => $"0x{cond:X}"
    };
}

/// <summary>
/// Classification of IR operand types.
/// </summary>
public enum IrOperandKind : byte
{
    /// <summary>General-purpose register (X0-X30, W0-W30, SP when Value=31).</summary>
    Register,
    /// <summary>FP/SIMD register (S0-S31, D0-D31, Q0-Q31, B0-B31, H0-H31).</summary>
    FpRegister,
    /// <summary>Integer constant value.</summary>
    Immediate,
    /// <summary>Floating-point constant (raw bits stored in Value).</summary>
    FloatImmediate,
    /// <summary>Memory reference [base + offset].</summary>
    Memory,
    /// <summary>Branch target or call target address.</summary>
    Label,
    /// <summary>Condition code (eq, ne, lt, gt, etc.).</summary>
    Condition,
    /// <summary>128-bit SIMD constant (raw bits stored in Value and Offset).</summary>
    SimdImmediate,
}
