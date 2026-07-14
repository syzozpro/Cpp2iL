namespace Rosetta.Analysis.IR.SSA;

/// <summary>
/// An SSA-versioned variable. Each assignment to a register creates a new version,
/// guaranteeing that every variable is defined exactly once.
///
/// GP registers (X0-X30 / W0-W30) use VarId = register number (0-30).
/// FP registers (S0-S31 / D0-D31) use VarId = 100 + register number.
/// Register 31 (SP) is NOT tracked in SSA — it's a stack pointer, not a data variable.
///
/// Example: Three assignments to X8 produce X8₀, X8₁, X8₂
/// </summary>
public struct SsaVariable : IEquatable<SsaVariable>
{
    /// <summary>Logical variable ID. GP: 0-30, FP: 100-131.</summary>
    public int VarId;

    /// <summary>SSA version (0 = initial/undefined, 1+ = assignments in program order).</summary>
    public int Version;

    /// <summary>Width in bits (8, 16, 32, 64, 128).</summary>
    public byte BitWidth;

    /// <summary>For SIMD registers: the width of a single element (e.g., 32 for float).</summary>
    public byte ElementWidth;

    /// <summary>For SIMD registers: the number of elements in the vector (e.g., 2, 4).</summary>
    public byte ElementCount;

    /// <summary>Is this variable holding an array?</summary>
    public bool IsArray;

    /// <summary>The size of the array.</summary>
    public int SizeArray;

    /// <summary>Is this variable holding a collection (List, Dictionary, etc.)?</summary>
    public bool IsCollection;

    /// <summary>Is the collection initialized (newed)?</summary>
    public bool IsInitialized;

    public SsaVariable(int varId, int version, byte bitWidth, byte elementWidth = 0, byte elementCount = 0)
    {
        VarId = varId;
        Version = version;
        BitWidth = bitWidth;
        ElementWidth = elementWidth;
        ElementCount = elementCount;
        IsArray = false;
        SizeArray = 0;
        IsCollection = false;
        IsInitialized = false;
    }

    /// <summary>Is this a floating-point variable?</summary>
    public bool IsFloat => VarId >= 100 && VarId < 200;

    /// <summary>Is this a stack slot variable (promoted via mem2reg)?</summary>
    public bool IsStackSlot => VarId >= 200;

    /// <summary>Original register number (strips the FP offset).</summary>
    public int RegisterNumber => IsFloat ? VarId - 100 : VarId;

    /// <summary>Display name: "x8_2", "s0_1", "local_spC_3".</summary>
    public string Name
    {
        get
        {
            if (IsStackSlot)
            {
                int offset = VarId - 200;
                return $"local_sp{offset:X}_{Version}";
            }

            string prefix;
            if (IsFloat)
            {
                if (ElementCount > 1)
                {
                    return $"v{RegisterNumber}_{Version}";
                }
                prefix = BitWidth switch { 32 => "s", 64 => "d", 128 => "q", _ => "v" };
            }
            else if (BitWidth > 32)
                prefix = "x"; // ARM64 64-bit
            else if (RegisterNumber <= 15)
                prefix = "R"; // ARM32 32-bit (R0-R15) or ARM64 32-bit (ambiguous but <= 15 is ARM32-friendly)
            else
                prefix = "w"; // ARM64 32-bit (W16-W30)
            return $"{prefix}{RegisterNumber}_{Version}";
        }
    }

    // ── Equality ───────────────────────────────────────────────────────────

    public bool Equals(SsaVariable other) => VarId == other.VarId && Version == other.Version;
    public override bool Equals(object? obj) => obj is SsaVariable v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(VarId, Version);
    public static bool operator ==(SsaVariable a, SsaVariable b) => a.Equals(b);
    public static bool operator !=(SsaVariable a, SsaVariable b) => !a.Equals(b);

    public override string ToString() => Name;

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Create a new version of the same variable.</summary>
    /// <summary>Create a new version of the same variable.</summary>
    public SsaVariable WithVersion(int newVersion)
    {
        var copy = new SsaVariable(VarId, newVersion, BitWidth, ElementWidth, ElementCount);
        copy.IsArray = IsArray;
        copy.SizeArray = SizeArray;
        copy.IsCollection = IsCollection;
        copy.IsInitialized = IsInitialized;
        return copy;
    }

    /// <summary>Check if this variable represents the same register (ignoring version).</summary>
    public bool SameRegister(SsaVariable other) => VarId == other.VarId;

    /// <summary>Invalid/undefined sentinel.</summary>
    public static readonly SsaVariable Undefined = new(-1, -1, 0);

    public bool IsUndefined => VarId < 0;
}
