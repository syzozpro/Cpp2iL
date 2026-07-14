using Rosetta.Model;

namespace Rosetta.Common;

/// <summary>
/// Shared type utilities for the decompiler pipeline.
/// Centralizes type name cleaning, element size resolution, and
/// system type alias mapping that was previously duplicated across
/// CodeGenStage, ExprPropagator/FieldHelpers, SsaAstBuilder, FieldRvaResolver,
/// and ArrayAccessHelper.
/// </summary>
public static class TypeUtils
{
    // ─── Parameter Modifier Stripping ────────────────────────────────────────

    /// <summary>Strip C# parameter modifiers (in/ref/out) and byref ampersand from a type name.
    /// "out int" → "int", "ref Vector3&" → "Vector3", "in float" → "float".</summary>
    public static string StripModifiers(string typeName)
    {
        if (typeName.StartsWith("in ", System.StringComparison.Ordinal)) typeName = typeName[3..];
        else if (typeName.StartsWith("ref ", System.StringComparison.Ordinal)) typeName = typeName[4..];
        else if (typeName.StartsWith("out ", System.StringComparison.Ordinal)) typeName = typeName[4..];
        if (typeName.EndsWith("&")) typeName = typeName[..^1];
        return typeName;
    }

    // ─── System Type → C# Alias ────────────────────────────────────────────

    /// <summary>Map a .NET fully qualified type name to its C# keyword alias.
    /// Examples: "System.Int32" → "int", "System.String[]" → "string[]".</summary>
    public static string ToAlias(string typeName)
    {
        var typeEnum = Rosetta.Analysis.Utils.TypeUtils.TypeHintToElementType(typeName);
        if (typeEnum != Il2CppTypeEnum.IL2CPP_TYPE_END)
        {
            var alias = typeEnum.ToCSharpKeyword();
            if (alias != null) return alias;
        }
        if (typeName == "System.Decimal" || typeName == "decimal") return "decimal";

        // Handle array types: "System.Int32[]" → "int[]", "System.Int32[,]" → "int[,]"
        int bracket = typeName.IndexOf('[');
        if (bracket > 0)
        {
            string elem = typeName[..bracket];
            string suffix = typeName[bracket..]; // "[]" or "[,]" etc.
            
            var nestedElemType = Rosetta.Analysis.Utils.TypeUtils.TypeHintToElementType(elem);
            if (nestedElemType != Il2CppTypeEnum.IL2CPP_TYPE_END)
            {
                var elemAlias = nestedElemType.ToCSharpKeyword();
                if (elemAlias != null) return elemAlias + suffix;
            }
            if (elem == "System.Decimal" || elem == "decimal") return "decimal" + suffix;
        }

        // Handle Nullable<T> → T?
        if (typeName.StartsWith("Nullable<") && typeName.EndsWith(">"))
        {
            string inner = typeName.Substring(9, typeName.Length - 10);
            return ToAlias(inner) + "?";
        }
        if (typeName.StartsWith("System.Nullable<") && typeName.EndsWith(">"))
        {
            string inner = typeName.Substring(16, typeName.Length - 17);
            return ToAlias(inner) + "?";
        }

        // Strip namespace: "UnityEngine.Vector3" → "Vector3"
        int dot = typeName.LastIndexOf('.');
        return dot >= 0 ? typeName[(dot + 1)..] : typeName;
    }

    /// <summary>Full type name cleaning: aliases + namespace stripping + backtick removal.
    /// Suitable for class headers, property types, field types, etc.</summary>
    public static string CleanTypeName(string name)
    {
        return CleanTypeName(name, out _);
    }

    public static string CleanTypeName(string name, out string nameSpace)
    {
        nameSpace = "";
        if (string.IsNullOrEmpty(name))
            return "";

        // Strip backtick arity from generic types (already has <T> from TypeResolver)
        int backtick = name.IndexOf('`');
        if (backtick >= 0)
        {
            int arityEnd = backtick + 1;
            while (arityEnd < name.Length && char.IsDigit(name[arityEnd]))
                arityEnd++;
            name = name[..backtick] + name[arityEnd..];
        }

        // Try alias first
        var typeEnum = Rosetta.Analysis.Utils.TypeUtils.TypeHintToElementType(name);
        if (typeEnum != Il2CppTypeEnum.IL2CPP_TYPE_END)
        {
            var alias = typeEnum.ToCSharpKeyword();
            if (alias != null)
            {
                nameSpace = "System";
                return alias;
            }
        }
        if (name == "System.Decimal" || name == "decimal")
        {
            nameSpace = "System";
            return "decimal";
        }

        // Handle pointers (e.g. "int*", "UnityEngine.UI.Text*")
        if (name.EndsWith("*"))
        {
            string baseClean = CleanTypeName(name[..^1], out nameSpace);
            return baseClean + "*";
        }

        // Handle arrays (e.g. "int[]", "UnityEngine.UI.Text[]", "int[,]")
        if (name.EndsWith("]"))
        {
            int openBracket = name.LastIndexOf('[');
            if (openBracket > 0)
            {
                string baseClean = CleanTypeName(name[..openBracket], out nameSpace);
                string bracketContent = name[openBracket..];
                return baseClean + bracketContent;
            }
        }

        // Handle generic types (e.g. "System.Collections.Generic.List<UnityEngine.UI.Text>")
        int openAngle = name.IndexOf('<');
        if (openAngle > 0 && name.EndsWith(">"))
        {
            string baseType = name[..openAngle];
            string baseClean = CleanTypeName(baseType, out nameSpace);

            string argsStr = name[(openAngle + 1)..^1];
            var args = SplitGenericArgs(argsStr);
            var cleanArgs = new System.Collections.Generic.List<string>();
            foreach (var arg in args)
            {
                cleanArgs.Add(CleanTypeName(arg, out _));
            }

            if ((baseClean == "Nullable" || baseClean == "System.Nullable") && cleanArgs.Count == 1)
            {
                return $"{cleanArgs[0]}?";
            }

            if (baseClean == "ValueTuple" || baseClean == "System.ValueTuple")
            {
                return $"({string.Join(", ", cleanArgs)})";
            }
            return $"{baseClean}<{string.Join(", ", cleanArgs)}>";
        }

        // Handle nested types: replace IL nested type separator '+' with C# '.'
        name = name.Replace('+', '.');

        // General dynamic namespace and class name extraction!
        // E.g. "UnityEngine.UI.Text" -> "Text" with namespace "UnityEngine.UI"
        int lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
        {
            nameSpace = name[..lastDot];
            return name[(lastDot + 1)..];
        }

        // No namespace prefix (e.g. "TestUI", "int")
        nameSpace = "";
        return name;
    }

    private static System.Collections.Generic.List<string> SplitGenericArgs(string argsStr)
    {
        var result = new System.Collections.Generic.List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < argsStr.Length; i++)
        {
            char c = argsStr[i];
            if (c == '<')
                depth++;
            else if (c == '>')
                depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(argsStr[start..i].Trim());
                start = i + 1;
            }
        }
        if (start < argsStr.Length)
        {
            result.Add(argsStr[start..].Trim());
        }
        return result;
    }

    // ─── Element Size ───────────────────────────────────────────────────────

    /// <summary>Get the byte size of a C# primitive type name.
    /// Returns 0 if the type is not a recognized primitive.</summary>
    public static int GetPrimitiveSize(string typeName)
    {
        return typeName switch
        {
            "bool" or "byte" or "sbyte" => 1,
            "short" or "ushort" or "char" => 2,
            "int" or "uint" or "float" => 4,
            "long" or "ulong" or "double" => 8,
            _ => 0
        };
    }

    /// <summary>Get the field size in bytes from type name or alias.</summary>
    public static int GetFieldSizeFromType(string? typeHint, Rosetta.Model.TypeModel? typeModel = null)
    {
        if (typeHint == null)
            throw new System.ArgumentNullException(nameof(typeHint));

        // 1. Resolve size dynamically via TypeModel metadata if available
        if (typeModel != null && typeModel.TryGetMetadataSize(typeHint, out int size, out _))
        {
            return size;
        }

        // 2. Fallback to hardcoded C# and .NET primitive mappings
        switch (typeHint)
        {
            case "byte": case "sbyte": case "bool":
            case "System.Byte": case "System.SByte": case "System.Boolean":
                return 1;
            case "short": case "ushort": case "char":
            case "System.Int16": case "System.UInt16": case "System.Char":
                return 2;
            case "int": case "uint": case "float":
            case "System.Int32": case "System.UInt32": case "System.Single":
                return 4;
            case "long": case "ulong": case "double":
            case "System.Int64": case "System.UInt64": case "System.Double":
                return 8;
            case "decimal": case "System.Decimal":
                return 16;
        }

        // 3. Fallback: arrays/pointers are reference pointers (8 bytes on 64-bit)
        if (IsArray(typeHint) || typeHint.EndsWith("*"))
        {
            return 8;
        }

        if (Rosetta.Pipeline.ConsoleReporter.Verbose)
        {
            Rosetta.Pipeline.ConsoleReporter.Warning($"[TYPEUTILS-WARN] Unknown type hint size: '{typeHint}'. Defaulting to 8 bytes.");
        }
        return 8;
    }


    // ─── Type Classification ────────────────────────────────────────────────

    /// <summary>Check if a type string represents a multi-dimensional array.</summary>
    public static bool IsMultiDimArray(string? typeName) => typeName != null && typeName.Contains("[,");

    /// <summary>Check if a type string represents a 1D array.</summary>
    public static bool Is1DArray(string? typeName) => typeName != null && typeName.EndsWith("[]") && !typeName.Contains(",");

    /// <summary>Check if a type string represents any array (1D or multi-dim).</summary>
    public static bool IsArray(string? typeName) => typeName != null && typeName.Contains('[');
       

    /// <summary>Extract the element type from an array type string.
    /// "int[]" → "int", "float[,]" → "float". Returns null if not an array type.</summary>
    public static string? GetArrayElementType(string? arrayType)
    {
        if (arrayType == null) return null;
        int bracket = arrayType.LastIndexOf('[');
        return bracket > 0 ? arrayType[..bracket] : null;
    }

    // ─── Floating-Point Formatting ──────────────────────────────────────────

    /// <summary>Format a float with the 'f' suffix and proper precision.</summary>
    public static string FormatFloat(float value)
    {
        if (float.IsNaN(value)) return "float.NaN";
        if (float.IsPositiveInfinity(value)) return "float.PositiveInfinity";
        if (float.IsNegativeInfinity(value)) return "float.NegativeInfinity";

        string s = value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E'))
            s += ".0";
        return s + "f";
    }

    /// <summary>Format a double with proper precision.</summary>
    public static string FormatDouble(double value)
    {
        if (double.IsNaN(value)) return "double.NaN";
        if (double.IsPositiveInfinity(value)) return "double.PositiveInfinity";
        if (double.IsNegativeInfinity(value)) return "double.NegativeInfinity";

        string s = value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E'))
            s += ".0";
        return s;
    }

    // ─── Generics Utilities ─────────────────────────────────────────────────

    /// <summary>
    /// Check if a type name is a generic placeholder like T, T[], TKey, TValue, etc.
    /// </summary>
    public static bool IsGenericPlaceholder(string typeName)
    {
        string baseName = typeName.TrimEnd('[', ']');
        return baseName is "T" or "TKey" or "TValue" or "TResult" or "TSource" or "TElement" or "TOutput" or "TInput";
    }

    /// <summary>
    /// Resolve a generic placeholder (e.g., "T" or "T[]") to a concrete type
    /// by extracting the type argument from a generic method name (e.g., "AddComponent&lt;BoxCollider&gt;").
    /// </summary>
    public static string ResolveGenericType(string placeholder, string methodName)
    {
        int genStart = methodName.IndexOf('<');
        int genEnd = methodName.LastIndexOf('>');
        if (genStart < 0 || genEnd <= genStart) return placeholder;

        string concreteType = methodName[(genStart + 1)..genEnd];

        // Preserve array suffix: T[] → BoxCollider[]
        if (placeholder.EndsWith("[]"))
            return concreteType + "[]";

        return concreteType;
    }

    /// <summary>
    /// Check if a type name corresponds to a standard .NET collection class
    /// (e.g., List, Dictionary, HashSet, Queue, Stack).
    /// </summary>
    public static bool IsStandardCollectionType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return false;

        string clean = typeName;
        int openAngle = clean.IndexOf('<');
        if (openAngle >= 0)
            clean = clean[..openAngle];

        int backtick = clean.IndexOf('`');
        if (backtick >= 0)
            clean = clean[..backtick];

        if (clean == "System.Collections.Generic.List" ||
            clean == "System.Collections.Generic.Dictionary" ||
            clean == "System.Collections.Generic.HashSet" ||
            clean == "System.Collections.Generic.Queue" ||
            clean == "System.Collections.Generic.Stack" ||
            clean == "System.Collections.ArrayList")
        {
            return true;
        }

        if (clean == "List" ||
            clean == "Dictionary" ||
            clean == "HashSet" ||
            clean == "Queue" ||
            clean == "Stack" ||
            clean == "ArrayList")
        {
            return true;
        }

        return false;
    }
}
