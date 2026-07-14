using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Reads Sections 21 (Assemblies), 22 (FieldRefs), 23 (ReferencedAssemblies).</summary>
/// <remarks>
/// Reference: Cpp2IL Il2CppAssemblyDefinition + Il2CppAssemblyNameDefinition
///
/// Il2CppAssemblyDefinition layout:
///   imageIndex(4)
///   [v24.1+] token(4)
///   [v38+]   moduleToken(4)
///   [v24 only] customAttributeIndex(4)
///   referencedAssemblyStart(4)
///   referencedAssemblyCount(4)
///   Il2CppAssemblyNameDefinition (inline):
///     nameIndex(4) + cultureIndex(4) + [<=v24.3] hashValueIndex(4) +
///     publicKeyIndex(4) + hashAlg(4) + hashLen(4) + flags(4) +
///     major(4) + minor(4) + build(4) + revision(4) + publicKeyToken(8)
/// </remarks>
public static class AssemblyReader
{
    public static AssemblyDef[] Read(EndianBinaryReader reader, MetadataSectionHeader section, int version = 39, int headerVersion = 39)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;

        int stride = ComputeStride(version, headerVersion, section.Size);
        int count = section.ItemCount > 0 ? section.ItemCount : (stride > 0 ? section.Size / stride : 0);
        if (count <= 0) return [];

        var items = new AssemblyDef[count];
        for (int i = 0; i < count; i++)
        {
            long startPos = reader.Position;

            int imageIndex = reader.ReadInt32();

            uint assemblyToken = 0;
            if (version >= 25) // v24.1+; since we only handle v24,v27,v29,v39 as integer versions
                assemblyToken = reader.ReadUInt32();

            uint moduleToken = 0;
            if (version >= 38) // v38+ only
                moduleToken = reader.ReadUInt32();

            if (version <= 24) // v24.0 only: customAttributeIndex
                reader.ReadInt32(); // skip

            int refAsmStart = reader.ReadInt32();
            int refAsmCount = reader.ReadInt32();

            // aname (inline struct)
            int nameIndex = reader.ReadInt32();
            int cultureIndex = reader.ReadInt32();

            if (headerVersion <= 24 && stride == 68) // hashValueIndex present in v<=24.3, absent in v24.15 and v24.2+
                reader.ReadInt32(); // skip

            int pubKeyDataIndex = reader.ReadInt32();
            uint hashAlgorithm = reader.ReadUInt32();
            int hashLength = reader.ReadInt32();
            uint attrs = reader.ReadUInt32();
            int major = reader.ReadInt32();
            int minor = reader.ReadInt32();
            int build = reader.ReadInt32();
            int revision = reader.ReadInt32();
            var pubKeyToken = new byte[8];
            for (int b = 0; b < 8; b++)
                pubKeyToken[b] = reader.ReadByte();

            items[i] = new AssemblyDef
            {
                ImageIndex = imageIndex,
                AssemblyToken = assemblyToken,
                ModuleToken = moduleToken,
                ReferencedAssemblyStart = refAsmStart,
                ReferencedAssemblyCount = refAsmCount,
                NameIndex = nameIndex,
                CultureIndex = cultureIndex,
                PublicKeyDataIndex = pubKeyDataIndex,
                HashAlgorithm = hashAlgorithm,
                HashLength = hashLength,
                Attributes = attrs,
                Major = major, Minor = minor, Build = build, Revision = revision,
                PublicKeyToken = pubKeyToken,
            };

            // Safety: skip to next record
            long expectedEnd = startPos + stride;
            if (reader.Position != expectedEnd)
                reader.Position = (int)expectedEnd;
        }
        return items;
    }

    /// <summary>Compute assembly struct stride per version.</summary>
    private static int ComputeStride(int version, int headerVersion, int sectionSize)
    {
        // Header part: imageIndex(4)
        int size = 4;
        if (version >= 25)  size += 4; // token
        if (version >= 38)  size += 4; // moduleToken
        if (version <= 24)  size += 4; // customAttributeIndex

        size += 4 + 4; // referencedAssemblyStart + Count

        // aname: nameIndex(4) + cultureIndex(4) + publicKeyIndex(4) +
        //        hashAlg(4) + hashLen(4) + flags(4) +
        //        major(4) + minor(4) + build(4) + revision(4) + pubKeyToken(8) = 48
        size += 48;

        if (headerVersion <= 24)
        {
            // Detect if hashValueIndex is present:
            // v24.0-v24.1 has hashValueIndex (stride 68)
            // v24.2+ does not have hashValueIndex (stride 64)
            if (sectionSize > 0 && sectionSize % 64 == 0 && sectionSize % 68 != 0)
            {
                // Stride is 64, do not add hashValueIndex
            }
            else
            {
                size += 4; // hashValueIndex in aname
            }
        }

        return size;
    }

    public static FieldRefDef[] ReadFieldRefs(EndianBinaryReader reader, MetadataSectionHeader section, IndexSizeResolver idx)
    {
        if (section.IsEmpty) return [];

        reader.Position = section.Offset;
        int stride = idx.TypeIndexSize + idx.FieldIndexSize;
        int count = section.ItemCount > 0 ? section.ItemCount : section.Size / stride;
        var items = new FieldRefDef[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = new FieldRefDef
            {
                TypeIndex = IndexReader.ReadSigned(reader, idx.TypeIndexSize),
                FieldIndex = IndexReader.ReadSigned(reader, idx.FieldIndexSize),
            };
        }
        return items;
    }

    public static int[] ReadReferencedAssemblies(EndianBinaryReader reader, MetadataSectionHeader section)
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
