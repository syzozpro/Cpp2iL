// Source of Truth: MetadataSerialization.cs, SerializedIndexSizes.cs
// Exact variable-width index sizing from decompiled IL2CPP source.

namespace Rosetta.Metadata;

/// <summary>
/// Computes the variable-width index sizes used within global-metadata.dat.
///
/// Source: MetadataSerialization.GetIndexSize():
///   ≤ 255    → 1 byte
///   ≤ 65535  → 2 bytes
///   > 65535  → 4 bytes
///
/// Variable-width index rollout per version:
///   v38:  TypeIndex, TypeDefinitionIndex, GenericContainerIndex (+ Type in binary)
///   v39:  + ParameterIndex
///   v104: + EventIndex, PropertyIndex, NestedTypeIndex, InterfaceOffsetIndex
///   v105: + MethodIndex
///   v106: + FieldIndex
/// </summary>
public sealed class IndexSizeResolver
{
    // ── v38+ indices ────────────────────────────────────────────────────
    /// <summary>Width of TypeIndex fields (from Il2CppType count in binary, solved from Field stride).</summary>
    public int TypeIndexSize { get; private set; }

    /// <summary>Width of TypeDefinitionIndex fields (from metadata TypeDefinitions count).</summary>
    public int TypeDefinitionIndexSize { get; private set; }

    /// <summary>Width of GenericContainerIndex fields (from GenericContainers count).</summary>
    public int GenericContainerIndexSize { get; private set; }

    // ── v39+ indices ────────────────────────────────────────────────────
    /// <summary>Width of ParameterIndex fields (from Parameters count). v38: fixed 4.</summary>
    public int ParameterIndexSize { get; private set; }

    // ── v104+ indices ───────────────────────────────────────────────────
    /// <summary>Width of EventIndex (into Events section). v104+: variable, earlier: 4.</summary>
    public int EventIndexSize { get; private set; }

    /// <summary>Width of PropertyIndex (into Properties section). v104+: variable, earlier: 4.</summary>
    public int PropertyIndexSize { get; private set; }

    /// <summary>Width of NestedTypeIndex (into NestedTypes section). v104+: variable, earlier: 4.</summary>
    public int NestedTypeIndexSize { get; private set; }

    /// <summary>Width of InterfaceOffsetIndex (into InterfaceOffsets section). v104+: variable, earlier: 4.</summary>
    public int InterfaceOffsetIndexSize { get; private set; }

    // ── v105+ indices ───────────────────────────────────────────────────
    /// <summary>Width of MethodIndex (into Methods section). v105+: variable, earlier: 4.</summary>
    public int MethodIndexSize { get; private set; }

    // ── v106+ indices ───────────────────────────────────────────────────
    /// <summary>Width of FieldIndex (into Fields section). v106+: variable, earlier: 4.</summary>
    public int FieldIndexSize { get; private set; }

    /// <summary>
    /// Compute index sizes from the header.
    /// V24-V29: All indices are fixed 4 bytes (no variable-width encoding).
    /// V38+: Solve variable-width sizes from section item counts.
    /// </summary>
    public IndexSizeResolver(GlobalMetadataHeader header)
    {
        if (header.Version < 38)
        {
            // Legacy (v23-v35): all indices are fixed int32
            TypeIndexSize = 4;
            TypeDefinitionIndexSize = 4;
            GenericContainerIndexSize = 4;
            ParameterIndexSize = 4;
            FieldIndexSize = 4;
            MethodIndexSize = 4;
            EventIndexSize = 4;
            PropertyIndexSize = 4;
            NestedTypeIndexSize = 4;
            InterfaceOffsetIndexSize = 4;
            return;
        }

        // ── v38+ core variable-width indices ────────────────────────────
        int genericContainerCount = header.Sections[GlobalMetadataHeader.GenericContainers].ItemCount;
        GenericContainerIndexSize = ComputeSize(genericContainerCount);

        int typeDefCount = header.Sections[GlobalMetadataHeader.TypeDefinitions].ItemCount;
        TypeDefinitionIndexSize = ComputeSize(typeDefCount);

        // ParameterIndex: v39+ variable, v38 fixed
        int parameterCount = header.Sections[GlobalMetadataHeader.Parameters].ItemCount;
        ParameterIndexSize = header.Version >= 39 ? ComputeSize(parameterCount) : 4;

        // ── TypeIndex: solve from Field section stride ──────────────────
        // FieldDef = nameIndex(4) + typeIndex(TI) + [customAttrIdx(4) v24] + token(4)
        // This is the most reliable way to compute TI since FieldDef layout
        // doesn't depend on the newer variable-width indices.
        var fieldSection = header.Sections[GlobalMetadataHeader.Fields];
        if (fieldSection.ItemCount > 0)
        {
            int fieldPerItem = fieldSection.Size / fieldSection.ItemCount;
            // For v25+: stride = 4 + TI + 4 = 8 + TI
            TypeIndexSize = fieldPerItem - 8;
            if (TypeIndexSize < 1 || TypeIndexSize > 4) TypeIndexSize = 4; // safety
        }
        else
        {
            // Fallback: try from TypeDef stride for pre-v104 (original approach)
            var tdSection = header.Sections[GlobalMetadataHeader.TypeDefinitions];
            if (tdSection.ItemCount > 0 && header.Version < 104)
            {
                int perItem = tdSection.Size / tdSection.ItemCount;
                int baseSize = header.Version >= 35 ? 68 : 72;
                int ti = (perItem - baseSize - GenericContainerIndexSize) / 3;
                TypeIndexSize = ti > 0 ? ti : 4;
            }
            else
            {
                TypeIndexSize = 4;
            }
        }

        // ── v104+ new variable-width indices ────────────────────────────
        if (header.Version >= 104)
        {
            int eventCount = header.Sections[GlobalMetadataHeader.Events].ItemCount;
            EventIndexSize = ComputeSize(eventCount);

            int propertyCount = header.Sections[GlobalMetadataHeader.Properties].ItemCount;
            PropertyIndexSize = ComputeSize(propertyCount);

            int nestedTypeCount = header.Sections[GlobalMetadataHeader.NestedTypes].ItemCount;
            NestedTypeIndexSize = ComputeSize(nestedTypeCount);

            int interfaceOffsetCount = header.Sections[GlobalMetadataHeader.InterfaceOffsets].ItemCount;
            InterfaceOffsetIndexSize = ComputeSize(interfaceOffsetCount);
        }
        else
        {
            EventIndexSize = 4;
            PropertyIndexSize = 4;
            NestedTypeIndexSize = 4;
            InterfaceOffsetIndexSize = 4;
        }

        // ── v105+ MethodIndex ───────────────────────────────────────────
        if (header.Version >= 105)
        {
            int methodCount = header.Sections[GlobalMetadataHeader.Methods].ItemCount;
            MethodIndexSize = ComputeSize(methodCount);
        }
        else
        {
            MethodIndexSize = 4;
        }

        // ── v106+ FieldIndex ────────────────────────────────────────────
        if (header.Version >= 106)
        {
            int fieldCount = header.Sections[GlobalMetadataHeader.Fields].ItemCount;
            FieldIndexSize = ComputeSize(fieldCount);
        }
        else
        {
            FieldIndexSize = 4;
        }
    }

    /// <summary>
    /// Direct constructor for known sizes.
    /// </summary>
    public IndexSizeResolver(int typeIndexSize, int typeDefIndexSize, int genericContainerIndexSize, int parameterIndexSize)
    {
        TypeIndexSize = typeIndexSize;
        TypeDefinitionIndexSize = typeDefIndexSize;
        GenericContainerIndexSize = genericContainerIndexSize;
        ParameterIndexSize = parameterIndexSize;
        FieldIndexSize = 4;
        MethodIndexSize = 4;
        EventIndexSize = 4;
        PropertyIndexSize = 4;
        NestedTypeIndexSize = 4;
        InterfaceOffsetIndexSize = 4;
    }

    /// <summary>
    /// Source: MetadataSerialization.GetIndexSize() — exact logic from decompiled source.
    /// </summary>
    public static int ComputeSize(int numberOfElements)
    {
        if (numberOfElements <= 255) return 1;
        if (numberOfElements <= 65535) return 2;
        return 4;
    }

    public override string ToString() =>
        $"IndexSizes [Type={TypeIndexSize}B, TypeDef={TypeDefinitionIndexSize}B, " +
        $"GenericContainer={GenericContainerIndexSize}B, Parameter={ParameterIndexSize}B, " +
        $"Field={FieldIndexSize}B, Method={MethodIndexSize}B, Event={EventIndexSize}B, " +
        $"Property={PropertyIndexSize}B, NestedType={NestedTypeIndexSize}B, InterfaceOffset={InterfaceOffsetIndexSize}B]";
}
