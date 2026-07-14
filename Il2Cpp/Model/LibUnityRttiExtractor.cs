// LibUnityRttiExtractor — Auto-extracts Transfer function field layouts from libunity.so
//
// Scans ARM64 code for ADRP+ADD patterns referencing serialization field name
// strings in .rodata. Clusters nearby references into Transfer functions.
//
// Output: List of NativeTransferInfo (ordered field names per Transfer function).
// No classID mapping here — that's done by the consumer using the serialized file's
// type entries, since the stripped binary has no deterministic classID→Transfer link.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Pipeline;

namespace Rosetta.ScriptsMap.Il2Cpp.Model;

/// <summary>
/// A Transfer function discovered in libunity.so: ordered field names.
/// </summary>
public sealed class NativeTransferInfo
{
    /// <summary>Unique ordered field names from this Transfer function.</summary>
    public required string[] Fields { get; init; }
    /// <summary>Code offset in the ELF (for debugging).</summary>
    public long CodeOffset { get; init; }
}

/// <summary>
/// Scans libunity.so ARM64 code to extract all Transfer function field layouts.
/// </summary>
public sealed class LibUnityRttiExtractor
{
    private long _textFileOff, _textVaddr, _textSize;
    private long _rodataStart, _rodataEnd;
    private byte[] _data = Array.Empty<byte>();

    /// <summary>
    /// Extract from raw libunity.so bytes.
    /// Returns all discovered Transfer functions with their field names.
    /// </summary>
    public List<NativeTransferInfo> Extract(byte[] libUnityData)
    {
        _data = libUnityData;
        if (!ParseElf())
        {
            ConsoleReporter.Warning("  Failed to parse libunity.so ELF");
            return new();
        }

        // Scan .text for ADRP+ADD pairs referencing field-name strings in .rodata
        var fieldRefs = ScanFieldReferences();
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    Field refs (m_*/PPtr): {fieldRefs.Count}");

        // Cluster nearby refs into Transfer functions (gap ≤ 0x400 = same function)
        var clusters = ClusterRefs(fieldRefs, maxGap: 0x400, minCount: 3);
        ConsoleReporter.Info($"  Found {clusters.Count} Transfer functions in libunity.so");

        // Build result
        var result = new List<NativeTransferInfo>(clusters.Count);
        foreach (var cluster in clusters)
        {
            var seen = new HashSet<string>();
            var fields = new List<string>();
            foreach (var (_, name) in cluster)
                if (seen.Add(name)) fields.Add(name);

            result.Add(new NativeTransferInfo
            {
                Fields = fields.ToArray(),
                CodeOffset = cluster[0].offset,
            });
        }

        return result;
    }

    /// <summary>
    /// Extract from APK: reads libunity.so from lib/arm64-v8a/.
    /// </summary>
    public List<NativeTransferInfo> ExtractFromApk(string apkPath)
    {
        ConsoleReporter.Phase("RTTI", "Extracting native RTTI from libunity.so");

        byte[]? soData = null;
        try
        {
            using var zip = new System.IO.Compression.ZipArchive(
                System.IO.File.OpenRead(apkPath), System.IO.Compression.ZipArchiveMode.Read);

            System.IO.Compression.ZipArchiveEntry? entry = null;
            foreach (var e in zip.Entries)
            {
                if (e.FullName.Contains("libunity.so"))
                {
                    if (e.FullName.Contains("arm64") || entry == null)
                        entry = e;
                }
            }

            if (entry == null)
            {
                ConsoleReporter.Warning("  libunity.so not found in APK");
                return new();
            }

            ConsoleReporter.Info($"  Reading {entry.FullName} ({entry.Length:N0} bytes)");
            using var stream = entry.Open();
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            soData = ms.ToArray();
        }
        catch (Exception ex)
        {
            ConsoleReporter.Warning($"  Failed to read APK: {ex.Message}");
            return new();
        }

        return Extract(soData);
    }

    // ═══════════════════════════════════════════════════════════════
    // ARM64 scanning
    // ═══════════════════════════════════════════════════════════════

    private List<(long offset, string name)> ScanFieldReferences()
    {
        var refs = new List<(long offset, string name)>();
        long textEnd = Math.Min(_textFileOff + _textSize, _data.Length - 8);

        for (long off = _textFileOff; off < textEnd; off += 4)
        {
            uint insn = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)off));
            if ((insn & 0x9F000000) != 0x90000000) continue; // ADRP

            int rd = (int)(insn & 0x1F);
            int immlo = (int)((insn >> 29) & 0x3);
            int immhi = (int)((insn >> 5) & 0x7FFFF);
            int imm = (immhi << 2) | immlo;
            if ((imm & 0x100000) != 0) imm -= 0x200000;

            long pcVaddr = _textVaddr + (off - _textFileOff);
            long pageAddr = ((pcVaddr >> 12) + imm) << 12;

            uint nextInsn = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)off + 4));
            if ((nextInsn & 0xFFC00000) != 0x91000000) continue; // ADD imm

            int addImm = (int)((nextInsn >> 10) & 0xFFF);
            int addRn = (int)((nextInsn >> 5) & 0x1F);
            if (addRn != rd) continue;

            long target = pageAddr | (uint)addImm;
            if (target < _rodataStart || target >= _rodataEnd) continue;

            string? s = ReadAsciiString((int)target);
            if (s != null && s.Length >= 3 && (s.StartsWith("m_") || s.StartsWith("PPtr")))
                refs.Add((off, s));
        }

        return refs;
    }

    // ═══════════════════════════════════════════════════════════════
    // Clustering
    // ═══════════════════════════════════════════════════════════════

    private static List<List<(long offset, string name)>> ClusterRefs(
        List<(long offset, string name)> refs, int maxGap, int minCount)
    {
        refs.Sort((a, b) => a.offset.CompareTo(b.offset));
        var clusters = new List<List<(long offset, string name)>>();
        var current = new List<(long offset, string name)>();

        foreach (var r in refs)
        {
            if (current.Count > 0 && r.offset - current[^1].offset > maxGap)
            {
                if (current.Count >= minCount) clusters.Add(current);
                current = new();
            }
            current.Add(r);
        }
        if (current.Count >= minCount) clusters.Add(current);
        return clusters;
    }

    // ═══════════════════════════════════════════════════════════════
    // ELF parsing
    // ═══════════════════════════════════════════════════════════════

    private bool ParseElf()
    {
        if (_data.Length < 64 || _data[0] != 0x7F || _data[1] != (byte)'E' ||
            _data[2] != (byte)'L' || _data[3] != (byte)'F' || _data[4] != 2)
            return false;

        long shoff = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(40));
        int shentsize = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(58));
        int shnum = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(60));
        int shstrndx = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(62));

        long shstrEntry = shoff + shstrndx * shentsize;
        long strTabOff = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan((int)shstrEntry + 24));

        for (int i = 0; i < shnum; i++)
        {
            long off = shoff + i * shentsize;
            int nameIdx = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan((int)off));
            long secOffset = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan((int)off + 24));
            long secSize = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan((int)off + 32));
            long secAddr = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan((int)off + 16));

            string? name = ReadAsciiString((int)(strTabOff + nameIdx));
            if (name == ".text")
            {
                _textFileOff = secOffset;
                _textVaddr = secAddr;
                _textSize = secSize;
            }
            else if (name == ".rodata")
            {
                _rodataStart = secOffset;
                _rodataEnd = secOffset + secSize;
            }
        }

        return _textSize > 0 && _rodataEnd > _rodataStart;
    }

    private string? ReadAsciiString(int offset)
    {
        if (offset < 0 || offset >= _data.Length) return null;
        int end = offset;
        while (end < _data.Length && _data[end] != 0) end++;
        int len = end - offset;
        if (len < 1 || len > 120) return null;
        var span = _data.AsSpan(offset, len);
        for (int i = 0; i < span.Length; i++)
            if (span[i] < 0x20 || span[i] > 0x7E) return null;
        return System.Text.Encoding.ASCII.GetString(span);
    }
}
