using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Reads Sections 6 (ParamDefaultValues), 7 (FieldDefaultValues), 8 (DefaultValuesData), 9 (FieldMarshaledSizes).</summary>
public static class DefaultValueReader
{
    public static ParameterDefaultValueDef[] ReadParamDefaults(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;
        int stride = idx.ParameterIndexSize + idx.TypeIndexSize + 4;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / stride;
        var items = new ParameterDefaultValueDef[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = new ParameterDefaultValueDef
            {
                ParameterIndex = IndexReader.ReadSigned(reader, idx.ParameterIndexSize),
                TypeIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize),
                DataIndex = reader.ReadInt32(),
            };
        }
        return items;
    }

    public static FieldDefaultValueDef[] ReadFieldDefaults(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;
        int stride = idx.FieldIndexSize + idx.TypeIndexSize + 4;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / stride;
        var items = new FieldDefaultValueDef[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = new FieldDefaultValueDef
            {
                FieldIndex = IndexReader.ReadSigned(reader, idx.FieldIndexSize),
                TypeIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize),
                DataIndex = reader.ReadInt32(),
            };
        }
        return items;
    }

    public static byte[] ReadDefaultValuesBlob(EndianBinaryReader reader, MetadataSectionHeader section)
    {
        if (section.IsEmpty) return [];
        return reader.Span.Slice(section.Offset, section.Size).ToArray();
    }

    public static FieldMarshaledSizeDef[] ReadMarshaledSizes(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;
        int stride = idx.FieldIndexSize + idx.TypeIndexSize + 4;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / stride;
        var items = new FieldMarshaledSizeDef[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = new FieldMarshaledSizeDef
            {
                FieldIndex = IndexReader.ReadSigned(reader, idx.FieldIndexSize),
                TypeIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize),
                Size = reader.ReadInt32(),
            };
        }
        return items;
    }
}
