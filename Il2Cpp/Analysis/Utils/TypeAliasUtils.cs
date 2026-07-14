namespace Rosetta.Analysis.Utils;

public static class TypeAliasUtils
{
    /// <summary>
    /// Converts a standard .NET System type name to its C# lowercase alias.
    /// Returns the original name if no alias exists.
    /// </summary>
    public static string GetCSharpAlias(string typeName)
    {
        var typeEnum = TypeUtils.TypeHintToElementType(typeName);
        if (typeEnum != Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_END)
        {
            var keyword = Rosetta.Common.Il2CppTypeEnumExtensions.ToCSharpKeyword(typeEnum);
            if (keyword != null) return keyword;
        }

        var withSystem = "System." + typeName;
        var withSystemEnum = TypeUtils.TypeHintToElementType(withSystem);
        if (withSystemEnum != Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_END)
        {
            var keyword = Rosetta.Common.Il2CppTypeEnumExtensions.ToCSharpKeyword(withSystemEnum);
            if (keyword != null) return keyword;
        }

        if (typeName == "System.Decimal" || typeName == "Decimal" || typeName == "decimal") return "decimal";
        return typeName;
    }

    /// <summary>Convert full .NET type name to short C# keyword for annotation encoding.</summary>
    public static string GetShortTypeName(string typeName)
    {
        var typeEnum = TypeUtils.TypeHintToElementType(typeName);
        if (typeEnum != Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_END)
        {
            if (typeEnum == Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I) return "nint";
            if (typeEnum == Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_U) return "nuint";
            var keyword = Rosetta.Common.Il2CppTypeEnumExtensions.ToCSharpKeyword(typeEnum);
            if (keyword != null) return keyword;
        }
        if (typeName == "System.Decimal" || typeName == "decimal") return "decimal";
        return "";
    }

    public struct ResolvedType
    {
        public string BaseType;
        public bool IsArray;
        public string[] GenericArguments;
        public string OriginalName;

        public static ResolvedType Parse(string typeName)
        {
            var res = new ResolvedType { OriginalName = typeName ?? "", BaseType = "", GenericArguments = System.Array.Empty<string>() };
            if (string.IsNullOrEmpty(typeName)) return res;

            string name = typeName;
            if (name.EndsWith("[]"))
            {
                res.IsArray = true;
                name = name[..^2];
            }

            int genericStart = name.IndexOf('<');
            if (genericStart > 0 && name.EndsWith(">"))
            {
                res.BaseType = name[..genericStart];
                string argsStr = name[(genericStart + 1)..^1];
                res.GenericArguments = SplitGenericArgs(argsStr);
            }
            else
            {
                res.BaseType = name;
            }

            return res;
        }

        private static string[] SplitGenericArgs(string argsStr)
        {
            var result = new System.Collections.Generic.List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < argsStr.Length; i++)
            {
                char c = argsStr[i];
                if (c == '<') depth++;
                else if (c == '>') depth--;
                else if (c == ',' && depth == 0)
                {
                    result.Add(argsStr[start..i].Trim());
                    start = i + 1;
                }
            }
            if (start < argsStr.Length)
                result.Add(argsStr[start..].Trim());
            return result.ToArray();
        }
    }
}
