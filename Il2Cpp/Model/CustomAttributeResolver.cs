// CustomAttributeResolver — Decodes IL2CPP v29+ custom attribute blobs.
//
// Self-contained within the Il2Cpp module. No PipelineContext dependency.
//
// Architecture (ported from Cpp2IL V29AttributeUtils + HasCustomAttributes):
//
//   1. Token Lookup: Binary search within image-scoped AttributeDataRanges
//      to find the blob offset for a given metadata token.
//
//   2. Blob Decode (per token):
//      - CompressedUInt32: attribute count
//      - uint32[count]:    constructor method indices (FIXED WIDTH)
//      - Per attribute:
//        - CompressedUInt32: numCtorArgs
//        - CompressedUInt32: numFields  (named field assignments)
//        - CompressedUInt32: numProps   (named property assignments)
//        - Each arg: byte(typeTag) + type-specific value
//
//   3. Unity Compressed Integers (NOT standard ECMA):
//      - b < 128            → 1-byte value
//      - b == 240           → full uint32 follows
//      - b == 254           → uint.MaxValue - 1
//      - b == 255           → uint.MaxValue
//      - (b & 0xC0) == 0xC0 → 4-byte
//      - (b & 0x80) == 0x80 → 2-byte

using System;
using System.Collections.Generic;
using System.Text;
using Rosetta.Binary;
using Rosetta.Common;
using Rosetta.Metadata;

namespace Rosetta.Model;

/// <summary>
/// Resolves custom attributes from IL2CPP v29+ metadata blobs.
/// Operates on raw metadata arrays — no PipelineContext needed.
/// </summary>
public sealed class CustomAttributeResolver
{
    private readonly MetadataParser _metadata;
    private readonly TypeResolver? _typeResolver;
    private readonly byte[] _attributeData;
    private readonly AttributeDataRangeDef[] _ranges;

    public CustomAttributeResolver(MetadataParser metadata, TypeResolver? typeResolver = null)
    {
        _metadata = metadata;
        _typeResolver = typeResolver;
        _attributeData = metadata.AttributeData;
        _ranges = metadata.AttributeDataRanges;
    }

    // ════════════════════════════════════════════════════════════════════
    // Public API — Returns formatted C# attribute strings
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve attributes for a metadata token, scoped to an image.
    /// Returns C# formatted strings like "[SerializeField]" or "[Range(0f, 100f)]".
    /// </summary>
    public List<string> Resolve(uint token, ImageDefinition image)
    {
        if (_attributeData.Length == 0 || _ranges.Length == 0)
            return [];

        if (_metadata.EffectiveVersion < 29)
        {
            return ResolvePre29(token, image);
        }

        // Binary search within the image's range (same as Cpp2IL)
        int rangeIdx = BinarySearchByToken(
            _ranges,
            image.CustomAttributeStart,
            (int)image.CustomAttributeCount,
            token);

        if (rangeIdx < 0)
            return [];

        // Get blob boundaries
        uint blobStart = _ranges[rangeIdx].StartOffset;
        uint blobEnd = (rangeIdx + 1 < _ranges.Length)
            ? _ranges[rangeIdx + 1].StartOffset
            : (uint)_attributeData.Length;

        if (blobStart >= _attributeData.Length || blobEnd <= blobStart)
            return [];

        try
        {
            return DecodeBlob(blobStart, blobEnd);
        }
        catch
        {
            // Never crash the pipeline for a malformed attribute
            return [];
        }
    }

    public bool HasAttribute(uint token, ImageDefinition image, string attributeFullName)
    {
        if (_attributeData.Length == 0 || _ranges.Length == 0)
            return false;

        if (_metadata.EffectiveVersion < 29)
        {
            return HasAttributePre29(token, image, attributeFullName);
        }

        int rangeIdx = BinarySearchByToken(
            _ranges,
            image.CustomAttributeStart,
            (int)image.CustomAttributeCount,
            token);

        if (rangeIdx < 0)
            return false;

        uint blobStart = _ranges[rangeIdx].StartOffset;
        uint blobEnd = (rangeIdx + 1 < _ranges.Length)
            ? _ranges[rangeIdx + 1].StartOffset
            : (uint)_attributeData.Length;

        if (blobStart >= _attributeData.Length || blobEnd <= blobStart)
            return false;

        int pos = (int)blobStart;
        int end = (int)blobEnd;
        try
        {
            uint attrCount = ReadCompressedUInt32(_attributeData, ref pos);
            if (attrCount == 0 || attrCount > 200)
                return false;

            var ctorIndices = new uint[attrCount];
            for (int i = 0; i < attrCount; i++)
            {
                if (pos + 4 > end) return false;
                ctorIndices[i] = BitConverter.ToUInt32(_attributeData, pos);
                pos += 4;
            }

            for (int i = 0; i < attrCount; i++)
            {
                uint ctorIdx = ctorIndices[i];
                if (ctorIdx >= _metadata.MethodDefinitions.Length)
                    continue;

                var ctor = _metadata.MethodDefinitions[(int)ctorIdx];
                if (ctor.DeclaringTypeIndex < 0 || ctor.DeclaringTypeIndex >= _metadata.TypeDefinitions.Length)
                    continue;

                if (_metadata.TypeDefinitions[ctor.DeclaringTypeIndex].FullName == attributeFullName)
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private List<string> ResolvePre29(uint token, ImageDefinition image)
    {
        var result = new List<string>();
        if (image.CustomAttributeCount == 0)
            return result;

        int rangeIdx = BinarySearchByToken(
            _ranges,
            image.CustomAttributeStart,
            (int)image.CustomAttributeCount,
            token);

        if (rangeIdx < 0)
            return result;

        var range = _ranges[rangeIdx];
        int start = range.Start;
        int count = range.Count;

        for (int i = 0; i < count; i++)
        {
            int attrIdx = start + i;
            if (attrIdx < 0 || attrIdx * 4 + 4 > _attributeData.Length)
                continue;

            int typeIdx = BitConverter.ToInt32(_attributeData, attrIdx * 4);
            if (_typeResolver != null)
            {
                try
                {
                    string typeName = _typeResolver.ResolveTypeName(typeIdx);
                    typeName = TypeUtils.CleanTypeName(typeName);
                    if (typeName.EndsWith("Attribute", StringComparison.Ordinal) && typeName.Length > 9)
                        typeName = typeName[..^9];
                    result.Add($"[{typeName}]");
                }
                catch { }
            }
            else
            {
                if (typeIdx >= 0 && typeIdx < _metadata.TypeDefinitions.Length)
                {
                    string typeName = _metadata.TypeDefinitions[typeIdx].Name ?? $"Type_{typeIdx}";
                    if (typeName.EndsWith("Attribute", StringComparison.Ordinal) && typeName.Length > 9)
                        typeName = typeName[..^9];
                    result.Add($"[{typeName}]");
                }
            }
        }
        return result;
    }

    private bool HasAttributePre29(uint token, ImageDefinition image, string attributeFullName)
    {
        if (image.CustomAttributeCount == 0)
            return false;

        int rangeIdx = BinarySearchByToken(
            _ranges,
            image.CustomAttributeStart,
            (int)image.CustomAttributeCount,
            token);

        if (rangeIdx < 0)
            return false;

        var range = _ranges[rangeIdx];
        int start = range.Start;
        int count = range.Count;

        for (int i = 0; i < count; i++)
        {
            int attrIdx = start + i;
            if (attrIdx < 0 || attrIdx * 4 + 4 > _attributeData.Length)
                continue;

            int typeIdx = BitConverter.ToInt32(_attributeData, attrIdx * 4);
            string? fullName = null;
            if (_typeResolver != null)
            {
                try
                {
                    fullName = _typeResolver.ResolveTypeName(typeIdx);
                }
                catch { }
            }
            else
            {
                if (typeIdx >= 0 && typeIdx < _metadata.TypeDefinitions.Length)
                    fullName = _metadata.TypeDefinitions[typeIdx].FullName;
            }

            if (fullName == attributeFullName)
                return true;
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════════════
    // Token binary search (port of Cpp2IL TokenComparer)
    // ════════════════════════════════════════════════════════════════════

    private static int BinarySearchByToken(AttributeDataRangeDef[] ranges, int start, int count, uint token)
    {
        if (count <= 0) return -1;

        int lo = start;
        int hi = start + count - 1;

        // Clamp to array bounds
        if (lo < 0) lo = 0;
        if (hi >= ranges.Length) hi = ranges.Length - 1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            uint midToken = ranges[mid].Token;

            if (midToken == token) return mid;
            if (midToken < token) lo = mid + 1;
            else hi = mid - 1;
        }

        return -1;
    }

    // ════════════════════════════════════════════════════════════════════
    // Blob Decoder
    // ════════════════════════════════════════════════════════════════════

    private List<string> DecodeBlob(uint blobStart, uint blobEnd)
    {
        var result = new List<string>();
        int pos = (int)blobStart;
        int end = (int)blobEnd;

        // 1. Attribute count (compressed)
        uint attrCount = ReadCompressedUInt32(_attributeData, ref pos);
        if (attrCount == 0 || attrCount > 200)
            return result;

        // 2. Constructor indices (FIXED WIDTH uint32, NOT compressed)
        var ctorIndices = new uint[attrCount];
        for (int i = 0; i < attrCount; i++)
        {
            if (pos + 4 > end) return result;
            ctorIndices[i] = BitConverter.ToUInt32(_attributeData, pos);
            pos += 4;
        }

        // 3. Per-attribute: decode args and format
        for (int i = 0; i < attrCount; i++)
        {
            string? formatted = DecodeAttribute(ctorIndices[i], _attributeData, ref pos, end);
            if (formatted != null)
                result.Add(formatted);
        }

        return result;
    }

    private string? DecodeAttribute(uint ctorIdx, byte[] data, ref int pos, int end)
    {
        // Resolve constructor → declaring type = attribute type
        if (ctorIdx >= _metadata.MethodDefinitions.Length)
            return null;

        var ctor = _metadata.MethodDefinitions[(int)ctorIdx];
        if (ctor.DeclaringTypeIndex < 0 || ctor.DeclaringTypeIndex >= _metadata.TypeDefinitions.Length)
            return null;

        var typeDef = _metadata.TypeDefinitions[ctor.DeclaringTypeIndex];
        string typeName = typeDef.Name ?? "Unknown";

        // Strip "Attribute" suffix per C# convention
        if (typeName.EndsWith("Attribute", StringComparison.Ordinal) && typeName.Length > 9)
            typeName = typeName[..^9];

        // Read argument counts (Cpp2IL: numCtorArgs, numFields, numProps)
        if (pos >= end) return $"[{typeName}]";
        uint numCtorArgs = ReadCompressedUInt32(data, ref pos);
        uint numFields = ReadCompressedUInt32(data, ref pos);
        uint numProps = ReadCompressedUInt32(data, ref pos);

        if (numCtorArgs + numFields + numProps == 0)
            return $"[{typeName}]";

        var args = new List<string>();

        // Constructor arguments (self-typed: each starts with type tag byte)
        for (uint a = 0; a < numCtorArgs && pos < end; a++)
        {
            string? val = ReadTypedValue(data, ref pos, end);
            if (val != null) args.Add(val);
        }

        // Named field assignments: value + fieldIndex
        for (uint f = 0; f < numFields && pos < end; f++)
        {
            string? val = ReadTypedValue(data, ref pos, end);
            int fieldIdx = ReadCompressedInt32(data, ref pos);
            string fieldName = ResolveFieldName(ctor, fieldIdx);
            if (val != null)
                args.Add($"{fieldName} = {val}");
        }

        // Named property assignments: value + propIndex
        for (uint p = 0; p < numProps && pos < end; p++)
        {
            string? val = ReadTypedValue(data, ref pos, end);
            int propIdx = ReadCompressedInt32(data, ref pos);
            string propName = ResolvePropName(ctor, propIdx);
            if (val != null)
                args.Add($"{propName} = {val}");
        }

        if (args.Count == 0)
            return $"[{typeName}]";

        return $"[{typeName}({string.Join(", ", args)})]";
    }

    // ════════════════════════════════════════════════════════════════════
    // Self-typed value reader (type tag byte + value)
    // Port of Cpp2IL V29AttributeUtils.ReadBlob
    // ════════════════════════════════════════════════════════════════════

    private string? ReadTypedValue(byte[] data, ref int pos, int end)
    {
        if (pos >= end) return null;

        var typeTag = (Il2CppTypeEnum)data[pos++];

        // For ENUM, read the enum type index first, then read as underlying primitive
        if (typeTag == Il2CppTypeEnum.IL2CPP_TYPE_ENUM)
        {
            int enumTypeIdx = ReadCompressedInt32(data, ref pos);
            // Resolve enum's underlying type and read that
            return ReadEnumValue(enumTypeIdx, data, ref pos, end);
        }

        return ReadValueByTag(typeTag, data, ref pos, end);
    }

    private string? ReadValueByTag(Il2CppTypeEnum tag, byte[] data, ref int pos, int end)
    {
        switch (tag)
        {
            case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
                if (pos >= end) return null;
                return data[pos++] != 0 ? "true" : "false";

            case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                if (pos + 2 > end) return null;
                char ch = BitConverter.ToChar(data, pos); pos += 2;
                return $"'{ch}'";

            case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                if (pos >= end) return null;
                return ((sbyte)data[pos++]).ToString();

            case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                if (pos >= end) return null;
                return data[pos++].ToString();

            case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                if (pos + 2 > end) return null;
                short s16 = BitConverter.ToInt16(data, pos); pos += 2;
                return s16.ToString();

            case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                if (pos + 2 > end) return null;
                ushort u16 = BitConverter.ToUInt16(data, pos); pos += 2;
                return u16.ToString();

            // I4/U4: Unity uses compressed format (NOT ReadInt32!)
            case Il2CppTypeEnum.IL2CPP_TYPE_I4:
                return ReadCompressedInt32(data, ref pos).ToString();

            case Il2CppTypeEnum.IL2CPP_TYPE_U4:
                return ReadCompressedUInt32(data, ref pos).ToString();

            case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                if (pos + 8 > end) return null;
                long i64 = BitConverter.ToInt64(data, pos); pos += 8;
                return i64.ToString() + "L";

            case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                if (pos + 8 > end) return null;
                ulong u64 = BitConverter.ToUInt64(data, pos); pos += 8;
                return u64.ToString() + "UL";

            case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                if (pos + 4 > end) return null;
                float f32 = BitConverter.ToSingle(data, pos); pos += 4;
                return FormatFloat(f32);

            case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                if (pos + 8 > end) return null;
                double f64 = BitConverter.ToDouble(data, pos); pos += 8;
                return f64.ToString("G");

            case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                return ReadString(data, ref pos, end);

            // typeof() argument — index into Il2CppType table (NOT TypeDefinition)
            case Il2CppTypeEnum.IL2CPP_TYPE_IL2CPP_TYPE_INDEX:
                int typeIdx = ReadCompressedInt32(data, ref pos);
                if (typeIdx < 0) return "null";
                string tn = ResolveTypeNameByTypeIndex(typeIdx);
                return $"typeof({tn})";

            // Unsupported (CLASS, GENERICINST, OBJECT — Cpp2IL throws for these too)
            default:
                return null;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Value Helpers
    // ════════════════════════════════════════════════════════════════════

    private string? ReadString(byte[] data, ref int pos, int end)
    {
        int length = ReadCompressedInt32(data, ref pos);
        if (length < 0) return "null";
        if (length == 0) return "\"\"";
        if (pos + length > end) return null;

        string value = Encoding.UTF8.GetString(data, pos, length);
        pos += length;
        return $"\"{EscapeString(value)}\"";
    }

    private string? ReadEnumValue(int enumTypeIdx, byte[] data, ref int pos, int end)
    {
        // Can't resolve — just read as int
        // In a full implementation we'd resolve the enum's underlying type
        // and match against field default values. For now, read as compressed int.
        return ReadCompressedInt32(data, ref pos).ToString();
    }

    private string ResolveFieldName(MethodDefinition ctor, int fieldIndex)
    {
        // Negative index = field on base type (need to read extra type index)
        // For now, just return the name if we can resolve it
        if (fieldIndex >= 0 && ctor.DeclaringTypeIndex >= 0)
        {
            var typeDef = _metadata.TypeDefinitions[ctor.DeclaringTypeIndex];
            int globalIdx = typeDef.FieldStart + fieldIndex;
            if (globalIdx >= 0 && globalIdx < _metadata.FieldDefinitions.Length)
                return _metadata.FieldDefinitions[globalIdx].Name ?? $"field_{fieldIndex}";
        }
        return $"field_{Math.Abs(fieldIndex)}";
    }

    private string ResolvePropName(MethodDefinition ctor, int propIndex)
    {
        if (propIndex >= 0 && ctor.DeclaringTypeIndex >= 0)
        {
            var typeDef = _metadata.TypeDefinitions[ctor.DeclaringTypeIndex];
            int globalIdx = typeDef.PropertyStart + propIndex;
            if (globalIdx >= 0 && globalIdx < _metadata.PropertyDefinitions.Length)
                return _metadata.PropertyDefinitions[globalIdx].Name ?? $"prop_{propIndex}";
        }
        return $"prop_{Math.Abs(propIndex)}";
    }

    /// <summary>
    /// Resolve a type name from an Il2CppType index (binary table).
    /// Falls back to TypeDefinition lookup if TypeResolver is unavailable.
    /// </summary>
    private string ResolveTypeNameByTypeIndex(int typeIdx)
    {
        // Use TypeResolver if available — it goes through the binary Il2CppType table
        if (_typeResolver != null)
        {
            try
            {
                string name = _typeResolver.ResolveTypeName(typeIdx);
                return TypeUtils.CleanTypeName(name);
            }
            catch { /* fall through */ }
        }

        // Fallback: treat as TypeDefinition index
        return ResolveTypeNameByDefinitionIndex(typeIdx);
    }

    private string ResolveTypeNameByDefinitionIndex(int typeDefIdx)
    {
        if (typeDefIdx >= 0 && typeDefIdx < _metadata.TypeDefinitions.Length)
            return _metadata.TypeDefinitions[typeDefIdx].Name ?? $"Type_{typeDefIdx}";
        return "object";
    }

    private static string FormatFloat(float f)
    {
        string s = f.ToString("G");
        // Ensure "f" suffix and decimal point
        if (!s.Contains('.') && !s.Contains('E'))
            return s + "f";
        return s + "f";
    }

    private static string EscapeString(string s)
        => s.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");

    // ════════════════════════════════════════════════════════════════════
    // Unity Compressed Integer Format
    // Port of Cpp2IL StreamExtensions.ReadUnityCompressedUint/Int
    //
    // DIFFERENT from standard ECMA compressed integers!
    // Has special sentinels: 240=full uint, 254=MaxValue-1, 255=MaxValue
    // ════════════════════════════════════════════════════════════════════

    private static uint ReadCompressedUInt32(byte[] data, ref int pos)
    {
        if (pos >= data.Length) return 0;

        byte b = data[pos++];

        if (b < 128)
            return b;

        if (b == 240)
        {
            // Full uint32 follows
            if (pos + 4 > data.Length) return 0;
            uint val = BitConverter.ToUInt32(data, pos);
            pos += 4;
            return val;
        }

        if (b == 255) return uint.MaxValue;
        if (b == 254) return uint.MaxValue - 1;

        if ((b & 0xC0) == 0xC0)
        {
            // 4-byte: (b & ~0xC0) << 24 | next 3 bytes
            if (pos + 3 > data.Length) return 0;
            return (uint)((b & ~0xC0U) << 24
                | (uint)(data[pos++] << 16)
                | (uint)(data[pos++] << 8)
                | data[pos++]);
        }

        if ((b & 0x80) == 0x80)
        {
            // 2-byte: (b & ~0x80) << 8 | next byte
            if (pos >= data.Length) return 0;
            return (uint)((b & ~0x80U) << 8 | data[pos++]);
        }

        return 0;
    }

    private static int ReadCompressedInt32(byte[] data, ref int pos)
    {
        uint unsigned = ReadCompressedUInt32(data, ref pos);

        if (unsigned == uint.MaxValue) return int.MinValue;

        bool isNeg = (unsigned & 1) == 1;
        unsigned >>= 1;
        return isNeg ? -(int)(unsigned + 1) : (int)unsigned;
    }
}
