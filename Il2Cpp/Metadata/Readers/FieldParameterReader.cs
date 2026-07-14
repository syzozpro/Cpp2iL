using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Reads FieldDefinition[] from Section 11 and ParameterDefinition[] from Section 10.</summary>
public static class FieldParameterReader
{
    public static FieldDefinition[] ReadFields(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx, int version = 39)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;

        // Field stride: nameIndex(4) + typeIndex(TI) + [customAttributeIndex(4) v24] + token(4)
        int stride = 4 + idx.TypeIndexSize + 4; // nameIndex + typeIndex + token
        if (version <= 24) stride += 4;           // customAttributeIndex

        int count = section.ItemCount > 0 ? section.ItemCount : (stride > 0 ? section.Size / stride : 0);
        if (count <= 0) return [];

        var defs = new FieldDefinition[count];

        for (int i = 0; i < count; i++)
        {
            defs[i] = new FieldDefinition
            {
                NameIndex = reader.ReadInt32(),
                TypeIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize),
            };

            // v24 only: customAttributeIndex
            if (version <= 24)
                reader.ReadInt32(); // skip

            defs[i].Token = reader.ReadUInt32();
        }
        return defs;
    }

    public static ParameterDefinition[] ReadParameters(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx, int version = 39)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;

        // Parameter stride: nameIndex(4) + token(4) + [customAttributeIndex(4) v24] + typeIndex(TI)
        int stride = 4 + 4 + idx.TypeIndexSize; // nameIndex + token + typeIndex
        if (version <= 24) stride += 4;           // customAttributeIndex

        int count = section.ItemCount > 0 ? section.ItemCount : (stride > 0 ? section.Size / stride : 0);
        if (count <= 0) return [];

        var defs = new ParameterDefinition[count];

        for (int i = 0; i < count; i++)
        {
            defs[i] = new ParameterDefinition
            {
                NameIndex = reader.ReadInt32(),
                Token = reader.ReadUInt32(),
            };

            // v24 only: customAttributeIndex
            if (version <= 24)
                reader.ReadInt32(); // skip

            defs[i].TypeIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize);
        }
        return defs;
    }
}
