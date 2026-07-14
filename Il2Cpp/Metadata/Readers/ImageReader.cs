using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Reads ImageDefinition[] from Section 20.</summary>
/// <remarks>
/// Reference: Cpp2IL Il2CppImageDefinition
///
/// Layout:
///   nameIndex(4) + assemblyIndex(4) + firstTypeIndex(TDI) + typeCount(4) +
///   [v24+] exportedTypeStart(TDI) + exportedTypeCount(4) +
///   entryPointIndex(MI) + token(4) +
///   [v24.1+] customAttributeStart(4) + customAttributeCount(4)
///
/// v105+: entryPointIndex uses variable-width MethodIndex.
/// </remarks>
public static class ImageReader
{
    public static ImageDefinition[] Read(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx, int version = 39)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;

        int stride = ComputeStride(idx, version);
        int count = section.ItemCount > 0 ? section.ItemCount : (stride > 0 ? section.Size / stride : 0);
        if (count <= 0) return [];

        var defs = new ImageDefinition[count];

        for (int i = 0; i < count; i++)
        {
            long startPos = reader.Position;
            defs[i] = new ImageDefinition
            {
                NameIndex = reader.ReadInt32(),
                AssemblyIndex = reader.ReadInt32(),
                TypeStart = IndexReader.ReadSigned(reader, idx.TypeDefinitionIndexSize),
                TypeCount = reader.ReadUInt32(),
            };

            if (version >= 24)
            {
                defs[i].ExportedTypeStart = IndexReader.ReadSigned(reader, idx.TypeDefinitionIndexSize);
                defs[i].ExportedTypeCount = reader.ReadUInt32();
            }

            defs[i].EntryPointIndex = IndexReader.ReadSigned(reader, idx.MethodIndexSize);
            defs[i].Token = reader.ReadUInt32();

            // v24.1+: customAttributeStart + customAttributeCount
            // Since we handle v24 as 24.0 (no .1), v25+ is safe
            if (version >= 25)
            {
                defs[i].CustomAttributeStart = reader.ReadInt32();
                defs[i].CustomAttributeCount = reader.ReadUInt32();
            }

            // Safety: skip to next record
            long expectedEnd = startPos + stride;
            if (reader.Position != expectedEnd)
                reader.Position = (int)expectedEnd;
        }
        return defs;
    }

    private static int ComputeStride(IndexSizeResolver idx, int version)
    {
        // nameIndex(4) + assemblyIndex(4) + firstTypeIndex(TDI) + typeCount(4)
        int size = 4 + 4 + idx.TypeDefinitionIndexSize + 4;

        if (version >= 24) // exportedTypeStart(TDI) + exportedTypeCount(4)
            size += idx.TypeDefinitionIndexSize + 4;

        size += idx.MethodIndexSize + 4; // entryPointIndex(MI) + token

        if (version >= 25) // v24.1+: customAttributeStart(4) + customAttributeCount(4)
            size += 4 + 4;

        return size;
    }
}
