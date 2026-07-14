using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Reads MethodDefinition[] from Section 5.</summary>
public static class MethodDefinitionReader
{
    public static MethodDefinition[] Read(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx, int version = 39)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;

        int stride = ComputeStride(idx, version);
        int count = section.ItemCount > 0 ? section.ItemCount : (stride > 0 ? section.Size / stride : 0);
        if (count <= 0) return [];

        var defs = new MethodDefinition[count];

        for (int i = 0; i < count; i++)
        {
            long startPos = reader.Position;
            defs[i] = new MethodDefinition
            {
                NameIndex = reader.ReadInt32(),
                DeclaringTypeIndex = IndexReader.ReadSigned(reader, idx.TypeDefinitionIndexSize),
                ReturnTypeIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize),
            };

            // v31+: returnParameterToken
            if (version >= 31)
                defs[i].ReturnParameterToken = reader.ReadUInt32();

            defs[i].ParameterStart = IndexReader.ReadSigned(reader, idx.ParameterIndexSize);

            // v24 only: customAttributeIndex
            if (version <= 24)
                reader.ReadInt32(); // customAttributeIndex — skip

            defs[i].GenericContainerIndex = IndexReader.ReadSigned(reader, idx.GenericContainerIndexSize);

            // v24 only: methodIndex, invokerIndex, delegateWrapperIndex, rgctxStartIndex, rgctxCount
            if (version <= 24)
            {
                reader.ReadInt32(); // methodIndex — skip
                reader.ReadInt32(); // invokerIndex — skip
                reader.ReadInt32(); // delegateWrapperIndex — skip
                reader.ReadInt32(); // rgctxStartIndex — skip
                reader.ReadInt32(); // rgctxCount — skip
            }

            defs[i].Token = reader.ReadUInt32();
            defs[i].Flags = reader.ReadUInt16();
            defs[i].IFlags = reader.ReadUInt16();
            defs[i].Slot = reader.ReadUInt16();
            defs[i].ParameterCount = reader.ReadUInt16();
            defs[i].GlobalIndex = i;

            // Safety: ensure we consumed exactly one stride
            long expectedEnd = startPos + stride;
            if (reader.Position != expectedEnd)
                reader.Position = (int)expectedEnd;
        }
        return defs;
    }

    /// <summary>Compute per-item byte size for each version.</summary>
    public static int ComputeStride(IndexSizeResolver idx, int version)
    {
        // Base: nameIndex(4) + declaringType(TDI) + returnType(TI) + parameterStart(PI) +
        //       genericContainerIndex(GCI) + token(4) + flags(2) + iflags(2) + slot(2) + parameterCount(2)
        int baseSize = 4 + idx.TypeDefinitionIndexSize + idx.TypeIndexSize + idx.ParameterIndexSize +
                        idx.GenericContainerIndexSize + 4 + 2 + 2 + 2 + 2;

        if (version >= 31)
            baseSize += 4; // returnParameterToken

        if (version <= 24)
            baseSize += 4 + 4 + 4 + 4 + 4 + 4; // customAttributeIndex + methodIndex + invokerIndex + delegateWrapperIndex + rgctxStart + rgctxCount

        return baseSize;
    }
}
