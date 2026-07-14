using System;

namespace Rosetta.Analysis.RegisterState;

/// <summary>
/// Information about what a register holds at a specific instruction point.
/// </summary>
public sealed class RegInfo
{
    /// <summary>What kind of value this register holds.</summary>
    public RegValueKind Kind { get; init; }

    /// <summary>C# type name if known (e.g., "System.String", "char[]", "int").</summary>
    public string? TypeName { get; init; }

    /// <summary>String value for literals, string constants, annotations.</summary>
    public string? Value { get; init; }

    /// <summary>Literal integer value (for Kind=Literal).</summary>
    public long IntValue { get; init; }

    /// <summary>Source register number (for Kind=Copied).</summary>
    public int SourceReg { get; init; } = -1;

    /// <summary>Base register for field access (for Kind=FieldValue).</summary>
    public int BaseReg { get; init; } = -1;

    /// <summary>Offset for field access.</summary>
    public long Offset { get; init; }

    /// <summary>Instruction index where this register was last defined.</summary>
    public int DefIndex { get; init; } = -1;

    /// <summary>Last bit-width used to write this register (32=w, 64=x).</summary>
    public byte DefBitWidth { get; init; }

    /// <summary>
    /// The canonical variable name to use for this register (e.g., "w8" or "x8"),
    /// based on the bit-width of the FIRST definition.
    /// </summary>
    public string? CanonicalName { get; init; }

    public override string ToString() => Kind switch
    {
        RegValueKind.This => "this",
        RegValueKind.Literal => $"{IntValue}",
        RegValueKind.StringLiteral => $"\"{Value}\"",
        RegValueKind.TypeOf => $"typeof({TypeName})",
        RegValueKind.CallResult => $"call→{TypeName ?? "?"}",
        RegValueKind.Copied => $"=reg{SourceReg}",
        RegValueKind.FieldValue => $"[reg{BaseReg}+0x{Offset:X}]",
        _ => Kind.ToString()
    };
}
