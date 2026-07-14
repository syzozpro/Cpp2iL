using Rosetta.IO;

namespace Rosetta.Metadata;

/// <summary>Reads null-terminated UTF-8 strings from the MetadataStrings blob (Section 2).</summary>
public sealed class StringHeap
{
    private readonly EndianBinaryReader _reader;
    private readonly int _offset;
    private readonly int _size;

    public StringHeap(EndianBinaryReader reader, MetadataSectionHeader section)
    {
        _reader = reader;
        _offset = section.Offset;
        _size = section.Size;
    }

    public string Read(int index)
    {
        if (index < 0 || index >= _size)
            return "<invalid_string>";
        return _reader.ReadStringAt(_offset + index);
    }
}
