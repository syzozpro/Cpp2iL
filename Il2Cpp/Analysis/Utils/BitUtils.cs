using System;
using Rosetta.Analysis.AST;
using Rosetta.Common;

namespace Rosetta.Analysis.Utils;

public static class BitUtils
{
    // ──────────────────────────────────────────────────────────────────
    // Extract raw 64-bit value from various ExprNode representations.
    // ──────────────────────────────────────────────────────────────────
    public static long? ExtractRawBits64(ExprNode v)
    {
        if (v is ExprLiteral l)
        {
            var parsed = TryParseNumericString(l.Value);
            if (parsed.HasValue) return parsed;
        }

        if (v is ExprVar ev)
        {
            var parsed = TryParseNumericString(ev.Name);
            if (parsed.HasValue) return parsed;
        }

        return null;
    }

    public static long? TryParseNumericString(object? val)
    {
        if (val is string s)
        {
            if (s.StartsWith("0x") && long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out long r)) return r;
            if (s.EndsWith("d") && double.TryParse(s[..^1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d))
                return BitConverter.DoubleToInt64Bits(d);
            if (s.EndsWith("f") && float.TryParse(s[..^1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float f))
                return (long)(ulong)(uint)BitConverter.SingleToInt32Bits(f);
        }
        else if (val is long lVal) return lVal;
        else if (val is int iVal) return iVal;
        else if (val is double dv) return BitConverter.DoubleToInt64Bits(dv);
        else if (val is float fv) return (long)(ulong)(uint)BitConverter.SingleToInt32Bits(fv);
        return null;
    }

    public static bool TryGetInteger(object? value, out long result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case uint u:
                result = u;
                return true;
            case ulong ul when ul <= long.MaxValue:
                result = (long)ul;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    public static bool TryGetInteger(ExprNode expr, out long result)
    {
        if (expr is ExprLiteral lit)
            return TryGetInteger(lit.Value, out result);

        result = 0;
        return false;
    }

    // ──────────────────────────────────────────────────────────────────
    // Raw bit extraction from packed store values
    // ──────────────────────────────────────────────────────────────────
    public static ExprNode ExtractFieldFromRaw(long rawVal, int bytePos, int fieldSize, Il2CppTypeEnum elementType)
    {
        ulong shifted = (ulong)rawVal >> (bytePos * 8);

        return elementType switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN => new ExprLiteral((shifted & 0xFF) != 0),
            Il2CppTypeEnum.IL2CPP_TYPE_I1 => new ExprLiteral((sbyte)(shifted & 0xFF)),
            Il2CppTypeEnum.IL2CPP_TYPE_U1 => new ExprLiteral((int)(shifted & 0xFF)),
            Il2CppTypeEnum.IL2CPP_TYPE_CHAR => MakeCharLiteral((char)(shifted & 0xFFFF)),
            Il2CppTypeEnum.IL2CPP_TYPE_I2 => new ExprLiteral((short)(shifted & 0xFFFF)),
            Il2CppTypeEnum.IL2CPP_TYPE_U2 => new ExprLiteral((int)(shifted & 0xFFFF)),
            Il2CppTypeEnum.IL2CPP_TYPE_R4 => new ExprLiteral(BitConverter.ToSingle(BitConverter.GetBytes((uint)(shifted & 0xFFFFFFFF)))),
            Il2CppTypeEnum.IL2CPP_TYPE_U4 => new ExprLiteral((uint)(shifted & 0xFFFFFFFF)),
            Il2CppTypeEnum.IL2CPP_TYPE_I4 => new ExprLiteral((int)(shifted & 0xFFFFFFFF)),
            Il2CppTypeEnum.IL2CPP_TYPE_R8 => new ExprLiteral(BitConverter.ToDouble(BitConverter.GetBytes((long)shifted))),
            Il2CppTypeEnum.IL2CPP_TYPE_U8 => new ExprLiteral((ulong)shifted),
            Il2CppTypeEnum.IL2CPP_TYPE_I8 => new ExprLiteral((long)shifted),
            _ => fieldSize switch
            {
                1 => new ExprLiteral((int)(shifted & 0xFF)),
                2 => new ExprLiteral((int)(shifted & 0xFFFF)),
                8 => new ExprLiteral((long)shifted),
                _ => new ExprLiteral((int)(shifted & 0xFFFFFFFF))
            }
        };
    }

    public static ExprNode MakeCharLiteral(char c)
    {
        if (c >= 0x20 && c < 0x7F && c != '\'' && c != '\\')
            return new ExprVar($"'{c}'");
        return new ExprVar($"'\\u{(int)c:X4}'");
    }

    public static ExprNode Decode32BitValue(uint raw, string? typeHint)
    {
        var result = typeHint switch
        {
            "int" => new ExprLiteral((int)raw),
            "uint" => new ExprLiteral(raw),
            "float" => new ExprLiteral(BitConverter.ToSingle(BitConverter.GetBytes(raw))),
            "bool" => new ExprLiteral(raw != 0),
            "short" => new ExprLiteral((short)(raw & 0xFFFF)),
            "ushort" => new ExprLiteral((ushort)(raw & 0xFFFF)),
            "byte" => new ExprLiteral((byte)(raw & 0xFF)),
            "sbyte" => new ExprLiteral((sbyte)(raw & 0xFF)),
            "char" => MakeCharLiteral((char)(raw & 0xFFFF)),
            null or "" => raw <= 0xFFFF
                ? new ExprLiteral((int)raw)
                : new ExprLiteral(BitConverter.ToSingle(BitConverter.GetBytes(raw))),
            _ => new ExprLiteral((int)raw)
        };
        return result;
    }
}