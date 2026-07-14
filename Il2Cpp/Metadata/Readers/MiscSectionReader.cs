using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Reads Sections 24 (AttributeData), 25 (AttributeDataRanges), 26-27 (UnresolvedVCalls), 30 (ExportedTypes).</summary>
public static class MiscSectionReader
{
    public static byte[] ReadAttributeData(EndianBinaryReader reader, MetadataSectionHeader section)
    {
        if (section.IsEmpty) return [];
        return reader.Span.Slice(section.Offset, section.Size).ToArray();
    }

    public static AttributeDataRangeDef[] ReadAttributeRanges(EndianBinaryReader reader, MetadataSectionHeader section, int version)
    {
        if (section.IsEmpty) return [];
        reader.Position = section.Offset;

        if (version >= 29)
        {
            int count = section.ItemCount > 0 ? section.ItemCount : section.Size / 8; // 8 = token(4) + startOffset(4)
            var items = new AttributeDataRangeDef[count];
            for (int i = 0; i < count; i++)
            {
                items[i] = new AttributeDataRangeDef
                {
                    Token = reader.ReadUInt32(),
                    StartOffset = reader.ReadUInt32(),
                };
            }
            return items;
        }
        else if (version >= 25) // promoted effective version for v24.2+
        {
            // v24.1+ / v25 to v28: stride 12 = token(4) + start(4) + count(4)
            int count = section.ItemCount > 0 ? section.ItemCount : section.Size / 12;
            var items = new AttributeDataRangeDef[count];
            for (int i = 0; i < count; i++)
            {
                items[i] = new AttributeDataRangeDef
                {
                    Token = reader.ReadUInt32(),
                    Start = reader.ReadInt32(),
                    Count = reader.ReadInt32()
                };
            }
            return items;
        }
        else
        {
            // v24.0 and below: stride 8 = start(4) + count(4)
            int count = section.ItemCount > 0 ? section.ItemCount : section.Size / 8;
            var items = new AttributeDataRangeDef[count];
            for (int i = 0; i < count; i++)
            {
                items[i] = new AttributeDataRangeDef
                {
                    Token = 0,
                    Start = reader.ReadInt32(),
                    Count = reader.ReadInt32()
                };
            }
            return items;
        }
    }

    public static int[] ReadUnresolvedVCallParamTypes(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx)
    {
        if (section.IsEmpty) return [];
        reader.Position = section.Offset;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / idx.TypeIndexSize;
        var items = new int[count];
        for (int i = 0; i < count; i++)
            items[i] = IndexReader.ReadSigned(reader, idx.TypeIndexSize);
        return items;
    }

    public static UnresolvedVCallRange[] ReadUnresolvedVCallRanges(EndianBinaryReader reader, MetadataSectionHeader section)
    {
        if (section.IsEmpty) return [];
        reader.Position = section.Offset;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / 8; // 8 = start(4) + length(4)
        var items = new UnresolvedVCallRange[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = new UnresolvedVCallRange
            {
                Start = reader.ReadInt32(),
                Length = reader.ReadInt32(),
            };
        }
        return items;
    }

    public static int[] ReadExportedTypes(EndianBinaryReader reader, MetadataSectionHeader section)
    {
        if (section.IsEmpty) return [];
        reader.Position = section.Offset;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / 4;
        var items = new int[count];
        for (int i = 0; i < count; i++)
            items[i] = reader.ReadInt32();
        return items;
    }
}
