// Source of Truth: Transpiler Omnibus §18.5
// Binary layout of Il2CppMethodDefinition in global-metadata.dat

namespace Rosetta.Metadata;

/// <summary>
/// Represents a method definition record from the Methods section
/// of global-metadata.dat.
/// Source: Transpiler Omnibus §18.5, §102
/// </summary>
public sealed class MethodDefinition
{
    /// <summary>Index into MetadataStrings section → method name.</summary>
    public int NameIndex { get; set; }

    /// <summary>Variable-width index: declaring TypeDefinition index.</summary>
    public int DeclaringTypeIndex { get; set; }

    /// <summary>Variable-width index: return type (Il2CppType index).</summary>
    public int ReturnTypeIndex { get; set; }

    /// <summary>Return parameter token.</summary>
    public uint ReturnParameterToken { get; set; }

    /// <summary>Variable-width index: first parameter in Parameters section.</summary>
    public int ParameterStart { get; set; }

    /// <summary>Variable-width index: generic container.</summary>
    public int GenericContainerIndex { get; set; }

    /// <summary>Metadata token (0x06XXXXXX for MethodDef).</summary>
    public uint Token { get; set; }

    /// <summary>ECMA-335 MethodAttributes flags.</summary>
    public ushort Flags { get; set; }

    /// <summary>ECMA-335 MethodImplAttributes flags.</summary>
    public ushort IFlags { get; set; }

    /// <summary>VTable slot index. 0xFFFF = no slot.</summary>
    public ushort Slot { get; set; }

    /// <summary>Number of parameters.</summary>
    public ushort ParameterCount { get; set; }

    // --- Resolved Names (populated during string resolution) ---
    public string? Name { get; set; }

    /// <summary>Index of this method within the global method array.</summary>
    public int GlobalIndex { get; set; }

    public override string ToString() => Name ?? $"Method_{GlobalIndex}";
}
