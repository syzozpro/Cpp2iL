using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Reads Sections 15 (NestedTypes), 16 (Interfaces), 17 (VTable), 18 (InterfaceOffsets).</summary>
public static class VTableReader
{
    public static int[] ReadNestedTypes(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx)
    {
        if (section.IsEmpty) return [];
        reader.Position = section.Offset;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / 4;
        var items = new int[count];
        for (int i = 0; i < count; i++)
            items[i] = reader.ReadInt32();
        return items;
    }

    public static int[] ReadInterfaces(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx)
    {
        if (section.IsEmpty) return [];
        reader.Position = section.Offset;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / idx.TypeIndexSize;
        var items = new int[count];
        for (int i = 0; i < count; i++)
            items[i] = IndexReader.ReadSigned(reader, idx.TypeIndexSize);
        return items;
    }

    public static uint[] ReadVTable(EndianBinaryReader reader, MetadataSectionHeader section)
    {
        if (section.IsEmpty) return [];
        reader.Position = section.Offset;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / 4;
        var items = new uint[count];
        for (int i = 0; i < count; i++)
            items[i] = reader.ReadUInt32();
        return items;
    }

    public static InterfaceOffsetPairDef[] ReadInterfaceOffsets(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx)
    {
        if (section.IsEmpty) return [];
        reader.Position = section.Offset;
        int stride = idx.TypeIndexSize + 4;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / stride;
        var items = new InterfaceOffsetPairDef[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = new InterfaceOffsetPairDef
            {
                InterfaceTypeIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize),
                Offset = reader.ReadInt32(),
            };
        }
        return items;
    }
}
