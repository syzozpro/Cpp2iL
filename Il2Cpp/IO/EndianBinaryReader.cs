using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Rosetta.IO;

/// <summary>
/// Zero-allocation binary reader over a <see cref="ReadOnlyMemory{T}"/> buffer.
/// All reads are little-endian (ARM64 native byte order).
/// </summary>
public sealed class EndianBinaryReader
{
    private ReadOnlyMemory<byte> _data;
    private int _position;

    public EndianBinaryReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public int Position
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _position;
        set
        {
            if ((uint)value > (uint)_data.Length)
                ThrowOutOfRange(value);
            _position = value;
        }
    }

    public int Length => _data.Length;

    public ReadOnlyMemory<byte> Memory => _data;

    public ReadOnlySpan<byte> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _data.Span;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        byte value = _data.Span[_position];
        _position += 1;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16()
    {
        short value = BinaryPrimitives.ReadInt16LittleEndian(_data.Span[_position..]);
        _position += 2;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16()
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Span[_position..]);
        _position += 2;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32()
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(_data.Span[_position..]);
        _position += 4;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32()
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Span[_position..]);
        _position += 4;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64()
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(_data.Span[_position..]);
        _position += 8;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64()
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Span[_position..]);
        _position += 8;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        var span = _data.Span.Slice(_position, count);
        _position += count;
        return span;
    }

    /// <summary>
    /// Read a variable-width index based on the index size.
    /// Source: Transpiler Omnibus §18.3.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadIndex(int indexSize)
    {
        return indexSize switch
        {
            1 => ReadByte(),
            2 => ReadUInt16(),
            4 => ReadInt32(),
            _ => throw new InvalidOperationException($"Invalid index size: {indexSize}")
        };
    }

    /// <summary>
    /// Read a null-terminated UTF-8 string at the given absolute offset in the buffer.
    /// Does NOT advance <see cref="Position"/>.
    /// </summary>
    public string ReadStringAt(int offset)
    {
        var span = _data.Span[offset..];
        int nullIndex = span.IndexOf((byte)0);
        if (nullIndex < 0)
            nullIndex = span.Length;
        return Encoding.UTF8.GetString(span[..nullIndex]);
    }

    /// <summary>
    /// Read a null-terminated UTF-8 string at the current position.
    /// Advances past the null terminator.
    /// </summary>
    public string ReadNullTerminatedString()
    {
        var span = _data.Span[_position..];
        int nullIndex = span.IndexOf((byte)0);
        if (nullIndex < 0)
            throw new InvalidDataException("Unterminated string in metadata");
        string result = Encoding.UTF8.GetString(span[..nullIndex]);
        _position += nullIndex + 1;
        return result;
    }

    /// <summary>
    /// Read an absolute uint32 at the given offset without moving the cursor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint PeekUInt32At(int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(_data.Span[offset..]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekInt32At(int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(_data.Span[offset..]);
    }

    private static void ThrowOutOfRange(int pos)
    {
        throw new ArgumentOutOfRangeException(nameof(pos), pos, "Position out of buffer range");
    }

    public void Clear()
    {
        _data = ReadOnlyMemory<byte>.Empty;
        _position = 0;
    }
}
