using Rosetta.Common;

namespace Rosetta.Analysis.Utils;

public static class TypeUtils
{
    // ── Convert annotation type-hint string → Il2CppTypeEnum ──
    public static Il2CppTypeEnum TypeHintToElementType(string? typeHint)
    {
        if (typeHint == null) return Il2CppTypeEnum.IL2CPP_TYPE_END;
        return typeHint switch
        {
            "bool"   or "System.Boolean" => Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
            "char"   or "System.Char"    => Il2CppTypeEnum.IL2CPP_TYPE_CHAR,
            "sbyte"  or "System.SByte"   => Il2CppTypeEnum.IL2CPP_TYPE_I1,
            "byte"   or "System.Byte"    => Il2CppTypeEnum.IL2CPP_TYPE_U1,
            "short"  or "System.Int16"   => Il2CppTypeEnum.IL2CPP_TYPE_I2,
            "ushort" or "System.UInt16"  => Il2CppTypeEnum.IL2CPP_TYPE_U2,
            "int"    or "System.Int32"   => Il2CppTypeEnum.IL2CPP_TYPE_I4,
            "uint"   or "System.UInt32"  => Il2CppTypeEnum.IL2CPP_TYPE_U4,
            "long"   or "System.Int64"   => Il2CppTypeEnum.IL2CPP_TYPE_I8,
            "ulong"  or "System.UInt64"  => Il2CppTypeEnum.IL2CPP_TYPE_U8,
            "float"  or "System.Single"  => Il2CppTypeEnum.IL2CPP_TYPE_R4,
            "double" or "System.Double"  => Il2CppTypeEnum.IL2CPP_TYPE_R8,
            "void"   or "System.Void"    => Il2CppTypeEnum.IL2CPP_TYPE_VOID,
            "string" or "System.String"  => Il2CppTypeEnum.IL2CPP_TYPE_STRING,
            "object" or "System.Object"  => Il2CppTypeEnum.IL2CPP_TYPE_OBJECT,
            "IntPtr" or "System.IntPtr" or "nint" => Il2CppTypeEnum.IL2CPP_TYPE_I,
            "UIntPtr" or "System.UIntPtr" or "nuint" => Il2CppTypeEnum.IL2CPP_TYPE_U,
            _ => Il2CppTypeEnum.IL2CPP_TYPE_END
        };
    }

    // ── Get the bit size from Il2CppTypeEnum (ECMA-335 standard sizes) ──
    public static int ElementTypeBitSize(Il2CppTypeEnum elementType) => elementType switch
    {
        Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or
        Il2CppTypeEnum.IL2CPP_TYPE_I1 or
        Il2CppTypeEnum.IL2CPP_TYPE_U1 => 8,

        Il2CppTypeEnum.IL2CPP_TYPE_CHAR or
        Il2CppTypeEnum.IL2CPP_TYPE_I2 or
        Il2CppTypeEnum.IL2CPP_TYPE_U2 => 16,

        Il2CppTypeEnum.IL2CPP_TYPE_I4 or
        Il2CppTypeEnum.IL2CPP_TYPE_U4 or
        Il2CppTypeEnum.IL2CPP_TYPE_R4 => 32,

        Il2CppTypeEnum.IL2CPP_TYPE_I8 or
        Il2CppTypeEnum.IL2CPP_TYPE_U8 or
        Il2CppTypeEnum.IL2CPP_TYPE_R8 => 64,

        _ => 32  // default: 4-byte aligned
    };

    // ── Get bit size from annotation type hint (routes through Il2CppTypeEnum) ──
    public static int GetFieldBitSize(string? typeHint) => ElementTypeBitSize(TypeHintToElementType(typeHint));
       
    /// <summary>
    /// Strips generic backtick-arity suffix from type names.
    /// E.g., "List`1" -> "List", "Dictionary`2" -> "Dictionary".
    /// </summary>
    public static string StripAritySuffix(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return typeName;

        int backtick = typeName.LastIndexOf('`');
        if (backtick >= 0)
        {
            // Find the end of the arity number (digits after the backtick)
            int arityEnd = backtick + 1;
            while (arityEnd < typeName.Length && char.IsDigit(typeName[arityEnd]))
                arityEnd++;
            return typeName[..backtick] + typeName[arityEnd..];
        }
        return typeName;
    }
}
