// Source of Truth: Transpiler Omnibus §18.4
// Binary layout of Il2CppTypeDefinition in global-metadata.dat
//
// Fixed fields: int32 + int32 + uint32 + int32×8 + uint16×8 + uint32×2
// Variable-width fields: ByvalTypeIndex, DeclaringTypeIndex, ParentIndex, GenericContainerIndex
//
// Note: The variable-width indices depend on collection sizes in the header.
// For Unity v39, we must calculate the index sizes from the section headers.

namespace Rosetta.Metadata;

/// <summary>
/// Represents a type definition record from the TypeDefinitions section
/// of global-metadata.dat.
/// Source: Transpiler Omnibus §18.4, §102
/// </summary>
public sealed class TypeDefinition
{
    /// <summary>Index into the MetadataStrings section → type name.</summary>
    public required int NameIndex { get; init; }

    /// <summary>Index into the MetadataStrings section → namespace.</summary>
    public required int NamespaceIndex { get; init; }

    /// <summary>Variable-width index: ByvalType → Il2CppType index.</summary>
    public int ByvalTypeIndex { get; set; }

    /// <summary>Variable-width index: declaring type (for nested types).</summary>
    public int DeclaringTypeIndex { get; set; }

    /// <summary>Variable-width index: parent type definition.</summary>
    public int ParentIndex { get; set; }

    /// <summary>Variable-width index: generic container (if generic).</summary>
    public int GenericContainerIndex { get; set; }

    /// <summary>ECMA-335 TypeAttributes flags.</summary>
    public uint Flags { get; set; }

    /// <summary>Start index into the Fields section.</summary>
    public int FieldStart { get; set; }

    /// <summary>Start index into the Methods section.</summary>
    public int MethodStart { get; set; }

    /// <summary>Start index into the Events section.</summary>
    public int EventStart { get; set; }

    /// <summary>Start index into the Properties section.</summary>
    public int PropertyStart { get; set; }

    /// <summary>Start index into the NestedTypes section.</summary>
    public int NestedTypesStart { get; set; }

    /// <summary>Start index into the Interfaces section.</summary>
    public int InterfacesStart { get; set; }

    /// <summary>Start index into the VTable section.</summary>
    public int VTableStart { get; set; }

    /// <summary>Start index into the InterfaceOffsets section.</summary>
    public int InterfaceOffsetsStart { get; set; }

    // Counts (uint16)
    public ushort MethodCount { get; set; }
    public ushort PropertyCount { get; set; }
    public ushort FieldCount { get; set; }
    public ushort EventCount { get; set; }
    public ushort NestedTypeCount { get; set; }
    public ushort VTableCount { get; set; }
    public ushort InterfacesCount { get; set; }
    public ushort InterfaceOffsetsCount { get; set; }

    /// <summary>Packed bitfield: value-type, enum, etc.</summary>
    public uint Bitfield { get; set; }

    /// <summary>Metadata token.</summary>
    public uint Token { get; set; }

    // --- Resolved Names (populated during string resolution) ---
    public string? Name { get; set; }
    public string? Namespace { get; set; }

    /// <summary>Full qualified name: Namespace.Name or just Name.</summary>
    public string FullName => string.IsNullOrEmpty(Namespace) ? (Name ?? "<unknown>") : $"{Namespace}.{Name}";

    // ────────────────────────────────────────────────────────────
    // Bitfield Computed Properties
    //
    // Source: TypeDefinitionsWriter.cs lines 58-72:
    //   num3 |= (typeDefinition.IsValueType ? 1 : 0);                       // bit 0
    //   num3 |= (int)((typeDefinition.IsEnum ? 1u : 0u) << 1);              // bit 1
    //   num3 |= (int)((typeDefinition.HasFinalizer() ? 1u : 0u) << 2);      // bit 2
    //   num3 |= (int)((typeDefinition.HasStaticConstructor ? 1u : 0u) << 3); // bit 3
    //   num3 |= (int)((IsBlittable ? 1u : 0u) << 4);                        // bit 4
    //   num3 |= (int)((IsComOrWindowsRuntime ? 1u : 0u) << 5);              // bit 5
    //   num3 |= (int)ConvertPackingSizeToCompressedEnum(alignmentPacking) << 6; // bits 6-9
    //   num3 |= (int)(((typeDefinition.PackingSize == -1) ? 1u : 0u) << 10); // bit 10
    //   num3 |= (int)(((typeDefinition.ClassSize == -1) ? 1u : 0u) << 11);   // bit 11
    //   num3 |= packingSizeEnum << 12;                                       // bits 12-15
    //   num3 |= (int)((typeDefinition.IsByRefLike ? 1u : 0u) << 16);        // bit 16
    // ────────────────────────────────────────────────────────────

    /// <summary>Bit 0: True if this type is a value type (struct/enum).</summary>
    public bool IsValueType => (Bitfield & 0x01) != 0;

    /// <summary>Bit 1: True if this type is an enum.</summary>
    public bool IsEnum => (Bitfield & 0x02) != 0;

    /// <summary>True if this type is a struct (value type that is NOT an enum).</summary>
    public bool IsStruct => IsValueType && !IsEnum;

    /// <summary>Bit 2: True if this type has a finalizer (~Destructor).</summary>
    public bool HasFinalizer => (Bitfield & 0x04) != 0;

    /// <summary>Bit 3: True if this type has a static constructor (.cctor).</summary>
    public bool HasStaticConstructor => (Bitfield & 0x08) != 0;

    /// <summary>Bit 4: True if this type is blittable (can be marshaled directly).</summary>
    public bool IsBlittable => (Bitfield & 0x10) != 0;

    /// <summary>Bit 5: True if this type is a COM or Windows Runtime type.</summary>
    public bool IsComOrWindowsRuntime => (Bitfield & 0x20) != 0;

    /// <summary>
    /// Bits 6-9: Alignment packing size (compressed enum).
    /// Source: TypeDefinitionsWriter.cs:112-127 — ConvertPackingSizeToCompressedEnum
    /// Values: 0=0, 1=1, 2=2, 3=4, 4=8, 5=16, 6=32, 7=64, 8=128
    /// </summary>
    public int AlignmentPackingSize => DecompressPackingSize((int)((Bitfield >> 6) & 0xF));

    /// <summary>Bit 10: True if PackingSize is -1 (default/unset).</summary>
    public bool PackingSizeIsDefault => (Bitfield & 0x400) != 0;

    /// <summary>Bit 11: True if ClassSize is -1 (default/unset).</summary>
    public bool ClassSizeIsDefault => (Bitfield & 0x800) != 0;

    /// <summary>
    /// Bits 12-15: Native packing size (compressed enum).
    /// Same encoding as AlignmentPackingSize.
    /// </summary>
    public int NativePackingSize => DecompressPackingSize((int)((Bitfield >> 12) & 0xF));

    /// <summary>Bit 16: True if this type is a ref struct (ByRefLike).</summary>
    public bool IsByRefLike => (Bitfield & 0x10000) != 0;

    /// <summary>
    /// Decompress a 4-bit packing size enum to the actual byte value.
    /// Source: TypeDefinitionsWriter.cs:112-127 — ConvertPackingSizeToCompressedEnum
    ///   0=Zero, 1=One, 2=Two, 3=Four, 4=Eight, 5=Sixteen, 6=ThirtyTwo, 7=SixtyFour, 8=OneHundredTwentyEight
    /// </summary>
    private static int DecompressPackingSize(int compressed) => compressed switch
    {
        0 => 0,
        1 => 1,
        2 => 2,
        3 => 4,
        4 => 8,
        5 => 16,
        6 => 32,
        7 => 64,
        8 => 128,
        _ => 0
    };

    public override string ToString() => FullName;
}

