using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Reads TypeDefinition[] from Section 19.</summary>
/// <remarks>
/// Reference: Cpp2IL Il2CppTypeDefinition
///
/// Layout:
///   nameIndex(4) + namespaceIndex(4) +
///   [v24 only] customAttributeIndex(4) +
///   byvalTypeIndex(TI) +
///   [<=v24.5] byrefTypeIndex(4) +
///   declaringTypeIndex(TI) + parentIndex(TI) +
///   [<=v34] elementTypeIndex(4) +
///   [<=v24.15] rgctxStartIndex(4) + rgctxCount(4) +
///   genericContainerIndex(GCI) +
///   flags(4) +
///   fieldStart(FI) + methodStart(MI) + eventStart(EI) + propertyStart(PI) +
///   nestedTypesStart(NI) + interfacesStart(IOI) + vtableStart(4) + interfaceOffsetsStart(IOI) +
///   8×ushort counts (16) +
///   bitfield(4) + token(4)
///
/// v104+: EI/PI/NI/IOI become variable-width
/// v105+: MI becomes variable-width
/// v106+: FI becomes variable-width
/// </remarks>
public static class TypeDefinitionReader
{
    public static TypeDefinition[] Read(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx, int version = 39, bool hasV24ByrefType = false)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;

        // Compute struct stride per version
        int stride = ComputeStride(idx, version, hasV24ByrefType);
        int count = section.ItemCount > 0 ? section.ItemCount : (stride > 0 ? section.Size / stride : 0);
        if (count <= 0) return [];

        var defs = new TypeDefinition[count];

        for (int i = 0; i < count; i++)
        {
            long startPos = reader.Position;
            defs[i] = new TypeDefinition
            {
                NameIndex = reader.ReadInt32(),
                NamespaceIndex = reader.ReadInt32(),
            };

            // [v24 only] customAttributeIndex
            if (version <= 24)
                reader.ReadInt32(); // skip

            defs[i].ByvalTypeIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize);

            // [<=v24.5] byrefTypeIndex — present in v24 and v24.2-v24.4, removed in v24.5+/v27+
            if (version <= 24 || hasV24ByrefType)
                reader.ReadInt32(); // skip

            defs[i].DeclaringTypeIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize);
            defs[i].ParentIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize);

            // [<=v34] elementTypeIndex — present in v24-v29, removed in v35+
            if (version < 35)
                reader.ReadInt32(); // skip (not mapped to TypeDefinition)

            // [<=v24.15] rgctxStartIndex + rgctxCount
            if (version <= 24)
            {
                reader.ReadInt32(); // rgctxStartIndex — skip
                reader.ReadInt32(); // rgctxCount — skip
            }

            defs[i].GenericContainerIndex = IndexReader.ReadSigned(reader, idx.GenericContainerIndexSize);

            defs[i].Flags = reader.ReadUInt32();

            // Start indices: v104+ uses variable-width for several of these
            defs[i].FieldStart = IndexReader.ReadSigned(reader, idx.FieldIndexSize);
            defs[i].MethodStart = IndexReader.ReadSigned(reader, idx.MethodIndexSize);
            defs[i].EventStart = IndexReader.ReadSigned(reader, idx.EventIndexSize);
            defs[i].PropertyStart = IndexReader.ReadSigned(reader, idx.PropertyIndexSize);
            defs[i].NestedTypesStart = IndexReader.ReadSigned(reader, idx.NestedTypeIndexSize);
            defs[i].InterfacesStart = IndexReader.ReadSigned(reader, idx.InterfaceOffsetIndexSize);
            defs[i].VTableStart = reader.ReadInt32(); // always int32
            defs[i].InterfaceOffsetsStart = IndexReader.ReadSigned(reader, idx.InterfaceOffsetIndexSize);

            defs[i].MethodCount = reader.ReadUInt16();
            defs[i].PropertyCount = reader.ReadUInt16();
            defs[i].FieldCount = reader.ReadUInt16();
            defs[i].EventCount = reader.ReadUInt16();
            defs[i].NestedTypeCount = reader.ReadUInt16();
            defs[i].VTableCount = reader.ReadUInt16();
            defs[i].InterfacesCount = reader.ReadUInt16();
            defs[i].InterfaceOffsetsCount = reader.ReadUInt16();
            defs[i].Bitfield = reader.ReadUInt32();
            defs[i].Token = reader.ReadUInt32();

            // Safety: ensure we consumed exactly one stride
            long expectedEnd = startPos + stride;
            if (reader.Position != expectedEnd)
                reader.Position = (int)expectedEnd;
        }
        return defs;
    }

    /// <summary>Compute per-item byte size for each version.</summary>
    public static int ComputeStride(IndexSizeResolver idx, int version, bool hasV24ByrefType = false)
    {
        // Base (all versions):
        //   nameIndex(4) + namespaceIndex(4) +
        //   byvalTypeIndex(TI) + declaringTypeIndex(TI) + parentIndex(TI) +
        //   genericContainerIndex(GCI) + flags(4) +
        //   fieldStart(FI) + methodStart(MI) + eventStart(EI) + propertyStart(PI) +
        //   nestedTypesStart(NI) + interfacesStart(IOI) + vtableStart(4) + interfaceOffsetsStart(IOI) +
        //   8×counts(16) + bitfield(4) + token(4)
        int baseSize = 4 + 4 +
                        idx.TypeIndexSize * 3 +
                        idx.GenericContainerIndexSize +
                        4 +
                        idx.FieldIndexSize + idx.MethodIndexSize +
                        idx.EventIndexSize + idx.PropertyIndexSize +
                        idx.NestedTypeIndexSize +
                        idx.InterfaceOffsetIndexSize + 4 + idx.InterfaceOffsetIndexSize +
                        16 + 4 + 4;

        // [<=v34] elementTypeIndex(4) — present in v24-v29, absent in v35+
        if (version < 35)
            baseSize += 4;

        // [v24 only] customAttributeIndex(4) + byrefTypeIndex(4) + rgctxStart(4) + rgctxCount(4)
        if (version <= 24)
            baseSize += 4 + 4 + 4 + 4;
        else if (hasV24ByrefType)
            baseSize += 4; // v24.2-v24.4: only byrefTypeIndex remains

        return baseSize;
    }
}
