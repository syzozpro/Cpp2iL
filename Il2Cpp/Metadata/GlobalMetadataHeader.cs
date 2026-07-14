// Source of Truth: Il2CppDumper MetadataClass.cs + WriteGlobalMetadataDat.cs
// V24-V29: Named offset+size pairs (8 bytes each) — Il2CppGlobalMetadataHeader struct
// V39: Generic section table with offset+size+itemCount (12 bytes × 31)

using Rosetta.Common;

namespace Rosetta.Metadata;

/// <summary>
/// Represents a single section header entry in the global-metadata.dat section table.
/// Each entry is 12 bytes in v39: { Offset(4), Size(4), ItemCount(4) }.
/// For v24-v29, ItemCount is computed from Size / structStride.
/// </summary>
public readonly record struct MetadataSectionHeader(int Offset, int Size, int ItemCount)
{
    public bool IsValid => Offset >= 0 && Size >= 0;
    public bool IsEmpty => Size == 0;
}

/// <summary>
/// The parsed header of global-metadata.dat.
///
/// CRITICAL: Two different header formats exist:
///   V24-V29: Named offset+size pairs (8 bytes each), variable field count per version.
///   V39: 12-byte section table × 31 sections.
///
/// Both formats are normalized into the same Sections[] array using the LOGICAL
/// section indices defined below. This allows all downstream readers to use
/// the same section indices regardless of metadata version.
/// </summary>
public sealed class GlobalMetadataHeader
{
    public required uint Magic { get; init; }
    public required int Version { get; init; }
    public required MetadataSectionHeader[] Sections { get; init; }

    // ======================================================================
    // LOGICAL section indices — same across all versions.
    // The header parser maps version-specific physical layouts to these.
    // ======================================================================

    // StringWriter (BaseManySectionsWriter → 2 sections)
    /// <summary>String literal offset table: (count+1) × 4 bytes.</summary>
    public const int StringLiteralOffsets = 0;
    /// <summary>String literal data: raw UTF-8 blob, ItemCount = literal count.</summary>
    public const int StringLiteralData = 1;

    // MetadataStringWriter (BaseSingleSectionWriter → 1 section)
    /// <summary>Null-terminated UTF-8 strings: class/method/namespace names.</summary>
    public const int MetadataStrings = 2;

    // EventWriter (BaseSingleSectionWriter → 1 section)
    public const int Events = 3;

    // PropertyWriter (BaseSingleSectionWriter → 1 section)
    public const int Properties = 4;

    // MethodWriter (BaseSingleSectionWriter → 1 section)
    public const int Methods = 5;

    // FieldAndParameterDataWriter (BaseManySectionsWriter → 4 sections)
    public const int ParameterDefaultValues = 6;
    public const int FieldDefaultValues = 7;
    public const int DefaultValuesData = 8;
    public const int FieldMarshaledSizes = 9;

    // ParameterWriter (BaseSingleSectionWriter → 1 section)
    public const int Parameters = 10;

    // FieldWriter (BaseSingleSectionWriter → 1 section)
    public const int Fields = 11;

    // GenericsDataWriter (BaseManySectionsWriter → 3 sections)
    public const int GenericParameters = 12;
    public const int GenericConstraints = 13;
    public const int GenericContainers = 14;

    // NestedTypesAndInterfacesWriter (BaseManySectionsWriter → 2 sections)
    public const int NestedTypes = 15;
    public const int Interfaces = 16;

    // VTableWriter (BaseSingleSectionWriter → 1 section)
    public const int VTableMethods = 17;

    // InterfaceOffsetsWriter (BaseSingleSectionWriter → 1 section)
    public const int InterfaceOffsets = 18;

    // TypeDefinitionsWriter
    public const int TypeDefinitions = 19;

    // AssemblyAndAttributeDataWriter (BaseManySectionsWriter → 6 sections)
    public const int Images = 20;
    public const int Assemblies = 21;
    public const int FieldRefs = 22;
    public const int ReferencedAssemblies = 23;
    public const int AttributeData = 24;
    public const int AttributeDataRanges = 25;

    // UnresolvedVirtualCallWriter (BaseManySectionsWriter → 2 sections)
    public const int UnresolvedVCParamTypes = 26;
    public const int UnresolvedVCParamRanges = 27;

    // WindowsRuntimeWriter (BaseManySectionsWriter → 2 sections)
    public const int WinRTTypeNamePairs = 28;
    public const int WinRTStrings = 29;

    // ExportedTypeWriter (BaseSingleSectionWriter → 1 section)
    public const int ExportedTypes = 30;

    // v104+: TypeInlineArraysWriter
    public const int TypeInlineArrays = 31;

    /// <summary>Total number of logical section slots. 32 to accommodate v104+ typeInlineArrays.</summary>
    public const int LogicalSectionCount = 32;
}
