using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Reads Sections 12 (GenericParameters), 13 (GenericConstraints), 14 (GenericContainers).</summary>
public static class GenericsReader
{
    public static GenericParameterDef[] ReadParameters(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;
        // Stride: ownerIndex(GCI) + nameIndex(4) + constraintsStart(2) + constraintsCount(2) + num(2) + flags(2)
        int stride = idx.GenericContainerIndexSize + 4 + 2 + 2 + 2 + 2;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / stride;
        var defs = new GenericParameterDef[count];

        for (int i = 0; i < count; i++)
        {
            defs[i] = new GenericParameterDef
            {
                OwnerIndex = IndexReader.ReadSigned(reader, idx.GenericContainerIndexSize),
                NameIndex = reader.ReadInt32(),
                ConstraintsStart = reader.ReadInt16(),
                ConstraintsCount = reader.ReadInt16(),
                Num = reader.ReadUInt16(),
                Flags = reader.ReadUInt16(),
            };
        }
        return defs;
    }

    public static int[] ReadConstraints(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / idx.TypeIndexSize;
        var items = new int[count];
        for (int i = 0; i < count; i++)
            items[i] = IndexReader.ReadSigned(reader, idx.TypeIndexSize);
        return items;
    }

    /// <summary>
    /// GenericContainers: 16 bytes each pre-v106, 11 bytes in v106+.
    /// v106 changes type_argc int32→uint16, is_method int32→uint8.
    /// </summary>
    public static GenericContainerDef[] ReadContainers(EndianBinaryReader reader, MetadataSectionHeader section, int version = 39)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;
        int stride = version >= 106 ? 11 : 16; // v106: 4+2+1+4=11; pre-v106: 4+4+4+4=16
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / stride;
        var defs = new GenericContainerDef[count];

        for (int i = 0; i < count; i++)
        {
            defs[i] = new GenericContainerDef
            {
                OwnerIndex = reader.ReadInt32(),
                Count = version >= 106 ? reader.ReadUInt16() : reader.ReadInt32(),
                IsMethod = version >= 106 ? reader.ReadByte() != 0 : reader.ReadInt32() != 0,
                ParameterStart = reader.ReadInt32(),
            };
        }
        return defs;
    }
}
