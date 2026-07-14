using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Reads PropertyDef[] (Section 4) and EventDef[] (Section 3).</summary>
public static class PropertyEventReader
{
    /// <summary>
    /// Properties: nameIndex(4) + get(MI) + set(MI) + attrs(4) + [customAttrIdx(4) v24] + token(4).
    /// v105+: get/set use variable-width MethodIndex.
    /// </summary>
    public static PropertyDef[] ReadProperties(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx, int version = 39)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;

        // Property stride: nameIndex(4) + get(MI) + set(MI) + attrs(4) + token(4)
        int stride = 4 + idx.MethodIndexSize + idx.MethodIndexSize + 4 + 4;
        if (version <= 24) stride += 4;    // customAttributeIndex

        int count = section.ItemCount > 0 ? section.ItemCount : (stride > 0 ? section.Size / stride : 0);
        if (count <= 0) return [];

        var defs = new PropertyDef[count];

        for (int i = 0; i < count; i++)
        {
            defs[i] = new PropertyDef
            {
                NameIndex = reader.ReadInt32(),
                Get = IndexReader.ReadSigned(reader, idx.MethodIndexSize),
                Set = IndexReader.ReadSigned(reader, idx.MethodIndexSize),
                Attrs = reader.ReadInt32(),
            };

            if (version <= 24)
                reader.ReadInt32(); // customAttributeIndex — skip

            defs[i].Token = reader.ReadUInt32();
        }
        return defs;
    }

    /// <summary>
    /// Events: nameIndex(4) + typeIndex(TI) + add(MI) + remove(MI) + raise(MI) + [customAttrIdx(4) v24] + token(4).
    /// v105+: add/remove/raise use variable-width MethodIndex.
    /// </summary>
    public static EventDef[] ReadEvents(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx, int version = 39)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;

        int stride = 4 + idx.TypeIndexSize + idx.MethodIndexSize * 3 + 4; // nameIndex + typeIndex + add + remove + raise + token
        if (version <= 24) stride += 4; // customAttributeIndex

        int count = section.ItemCount > 0 ? section.ItemCount : (stride > 0 ? section.Size / stride : 0);
        if (count <= 0) return [];

        var defs = new EventDef[count];

        for (int i = 0; i < count; i++)
        {
            defs[i] = new EventDef
            {
                NameIndex = reader.ReadInt32(),
                TypeIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize),
                Add = IndexReader.ReadSigned(reader, idx.MethodIndexSize),
                Remove = IndexReader.ReadSigned(reader, idx.MethodIndexSize),
                Raise = IndexReader.ReadSigned(reader, idx.MethodIndexSize),
            };

            if (version <= 24)
                reader.ReadInt32(); // customAttributeIndex — skip

            defs[i].Token = reader.ReadUInt32();
        }
        return defs;
    }
}
