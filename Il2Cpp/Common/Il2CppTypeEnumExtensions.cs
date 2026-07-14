namespace Rosetta.Common;

/// <summary>
/// Maps Il2CppTypeEnum values to C# keyword names.
/// Source: ECMA-335 element type signatures → C# language spec keywords.
/// </summary>
public static class Il2CppTypeEnumExtensions
{
    public static string? ToCSharpKeyword(this Il2CppTypeEnum type) => type switch
    {
        Il2CppTypeEnum.IL2CPP_TYPE_VOID    => "void",
        Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN => "bool",
        Il2CppTypeEnum.IL2CPP_TYPE_CHAR    => "char",
        Il2CppTypeEnum.IL2CPP_TYPE_I1      => "sbyte",
        Il2CppTypeEnum.IL2CPP_TYPE_U1      => "byte",
        Il2CppTypeEnum.IL2CPP_TYPE_I2      => "short",
        Il2CppTypeEnum.IL2CPP_TYPE_U2      => "ushort",
        Il2CppTypeEnum.IL2CPP_TYPE_I4      => "int",
        Il2CppTypeEnum.IL2CPP_TYPE_U4      => "uint",
        Il2CppTypeEnum.IL2CPP_TYPE_I8      => "long",
        Il2CppTypeEnum.IL2CPP_TYPE_U8      => "ulong",
        Il2CppTypeEnum.IL2CPP_TYPE_R4      => "float",
        Il2CppTypeEnum.IL2CPP_TYPE_R8      => "double",
        Il2CppTypeEnum.IL2CPP_TYPE_STRING  => "string",
        Il2CppTypeEnum.IL2CPP_TYPE_OBJECT  => "object",
        Il2CppTypeEnum.IL2CPP_TYPE_I       => "System.IntPtr",
        Il2CppTypeEnum.IL2CPP_TYPE_U       => "System.UIntPtr",
        Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF => "TypedReference",
        _ => null,
    };
}
