using System;

namespace Rosetta.Common;

/// <summary>
/// Centralized helper for global-metadata decoding operations.
/// </summary>
public static class MetadataDecoder
{
    /// <summary>
    /// Reads a compressed unsigned 32-bit integer (ECMA-335 II.23.2).
    /// </summary>
    public static bool TryReadCompressedUInt32(byte[] blob, ref int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset >= blob.Length)
            return false;

        try
        {
            byte b1 = blob[offset++];
            if ((b1 & 0x80) == 0)
            {
                value = b1;
            }
            else if ((b1 & 0xC0) == 0x80)
            {
                if (offset >= blob.Length) return false;
                value = (uint)(((b1 & 0x3F) << 8) | blob[offset++]);
            }
            else if ((b1 & 0xE0) == 0xC0)
            {
                if (offset + 2 >= blob.Length) return false;
                value = (uint)(((b1 & 0x1F) << 24) | (blob[offset++] << 16) | (blob[offset++] << 8) | blob[offset++]);
            }
            else if (b1 == 0xF0)
            {
                if (offset + 3 >= blob.Length) return false;
                value = BitConverter.ToUInt32(blob, offset);
                offset += 4;
            }
            else
            {
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes a zigzag-encoded integer to its original signed/unsigned value.
    /// </summary>
    public static long DecodeZigZag(uint rawValue, bool isSigned)
    {
        if (isSigned)
        {
            return (long)(rawValue >> 1) ^ -(long)(rawValue & 1);
        }
        else
        {
            return rawValue;
        }
    }
}
