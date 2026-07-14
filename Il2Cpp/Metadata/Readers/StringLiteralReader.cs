using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Reads string literals from Sections 0 (offsets/defs) + 1 (data).</summary>
/// <remarks>
/// Pre-v35: Section 0 contains (length:uint32, dataIndex:int32) pairs. Each literal is read at data[dataIndex..dataIndex+length].
/// V35+: Section 0 contains (dataIndex:int32) only. Each literal's length is determined by next entry or end of data section.
/// </remarks>
public static class StringLiteralReader
{
    public static string[] Read(EndianBinaryReader reader, MetadataSectionHeader offsetSection, MetadataSectionHeader dataSection, int version = 39)
    {
        if (offsetSection.IsEmpty || dataSection.IsEmpty) return [];

        if (version >= 35)
            return ReadV35(reader, offsetSection, dataSection);
        else
            return ReadLegacy(reader, offsetSection, dataSection);
    }

    /// <summary>V35+: Each entry is just dataIndex(4). Length = next.dataIndex - this.dataIndex.</summary>
    private static string[] ReadV35(EndianBinaryReader reader, MetadataSectionHeader offsetSection, MetadataSectionHeader dataSection)
    {
        reader.Position = offsetSection.Offset;
        int stride = 4; // just dataIndex
        int count = offsetSection.ItemCount > 0 ? offsetSection.ItemCount : offsetSection.Size / stride;
        if (count <= 0) return [];

        var dataIndices = new int[count];
        for (int i = 0; i < count; i++)
            dataIndices[i] = reader.ReadInt32();

        var strings = new string[count];
        ReadOnlySpan<byte> dataSpan = reader.Span.Slice(dataSection.Offset, dataSection.Size);
        for (int i = 0; i < count; i++)
        {
            int start = dataIndices[i];
            int end = (i + 1 < count) ? dataIndices[i + 1] : dataSection.Size;
            int len = end - start;
            if (start >= 0 && len >= 0 && end <= dataSection.Size)
                strings[i] = System.Text.Encoding.UTF8.GetString(dataSpan.Slice(start, len));
            else
                strings[i] = $"<invalid_literal_{i}>";
        }
        return strings;
    }

    /// <summary>Pre-v35: Each entry is (length:uint32, dataIndex:int32) = 8 bytes.</summary>
    private static string[] ReadLegacy(EndianBinaryReader reader, MetadataSectionHeader offsetSection, MetadataSectionHeader dataSection)
    {
        reader.Position = offsetSection.Offset;
        int stride = 8; // length(4) + dataIndex(4)
        int count = offsetSection.ItemCount > 0 ? offsetSection.ItemCount : offsetSection.Size / stride;
        if (count <= 0) return [];

        var strings = new string[count];
        ReadOnlySpan<byte> dataSpan = reader.Span.Slice(dataSection.Offset, dataSection.Size);
        for (int i = 0; i < count; i++)
        {
            uint length = reader.ReadUInt32();
            int dataIndex = reader.ReadInt32();
            if (dataIndex >= 0 && length >= 0 && dataIndex + length <= dataSection.Size)
                strings[i] = System.Text.Encoding.UTF8.GetString(dataSpan.Slice(dataIndex, (int)length));
            else
                strings[i] = $"<invalid_literal_{i}>";
        }
        return strings;
    }
}
