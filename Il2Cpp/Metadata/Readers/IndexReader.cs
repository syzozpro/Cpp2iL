using Rosetta.IO;

namespace Rosetta.Metadata.Readers;

/// <summary>Shared helper for reading variable-width indices with sentinel handling.</summary>
internal static class IndexReader
{
    /// <summary>Read a variable-width signed index. -1 sentinel: 0xFF/0xFFFF/0xFFFFFFFF.</summary>
    public static int ReadSigned(EndianBinaryReader reader, int indexSize)
    {
        if (indexSize == 1)
        {
            byte b = reader.ReadByte();
            return b == 0xFF ? -1 : b;
        }
        if (indexSize == 2)
        {
            ushort v = reader.ReadUInt16();
            return v == 0xFFFF ? -1 : v;
        }
        uint u = reader.ReadUInt32();
        return u == 0xFFFFFFFF ? -1 : (int)u;
    }
}
