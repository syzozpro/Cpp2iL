using System;
using System.Collections.Generic;
using Rosetta.Common;

namespace Rosetta.Analysis.Utils;

/// <summary>Field/type name cleaning and type-hint extraction utilities.</summary>
public static class StringUtils
{
    private static readonly HashSet<string> NonNullStringMethods = new(StringComparer.Ordinal)
    {
        "Concat", "Format", "Join", "Replace", "Split", "Substring",
        "ToLower", "ToLowerInvariant", "ToUpper", "ToUpperInvariant",
        "Trim", "TrimEnd", "TrimStart", "ToString"
    };

    /// <summary>Checks if the method name is a standard string method that always returns a non-null value.</summary>
    public static bool IsNonNullStringMethod(this string methodName)
    {
        return NonNullStringMethods.Contains(methodName);
    }
    public static string CleanTypeName(string raw) => Rosetta.Common.TypeUtils.ToAlias(raw);
    
    public static string CleanTypeNameAndAddUsing(string raw, System.Collections.Generic.ICollection<string>? usings)
    {
        string cleaned = Rosetta.Common.TypeUtils.CleanTypeName(raw, out string ns);
        if (usings != null && !string.IsNullOrEmpty(ns))
        {
            if (!usings.Contains(ns))
            {
                usings.Add(ns);
            }
        }
        return Rosetta.Common.TypeUtils.ToAlias(cleaned);
    }

    public static string CleanMethodName(string raw) => raw;

    public static string CleanFieldName(string name, Rosetta.Model.TypeModel? typeModel = null)
    {
        // String literals should never be cleaned
        if (name.StartsWith("\"")) return name;

        // Strip adjacent field encoding (e.g., "this.privateVar:int+this.publicVar:float" → "this.privateVar:int")
        int plusIdx = name.IndexOf('+');
        if (plusIdx > 0)
            name = name[..plusIdx];

        // Strip type hint suffix (e.g., "this.privateVar:int" → "this.privateVar")
        int colonIdx = name.LastIndexOf(':');
        if (colonIdx > 0 && colonIdx < name.Length - 1 && !name.Contains("::"))
            name = name[..colonIdx];

        if (name.Contains("k__BackingField"))
        {
            bool hasThis = name.StartsWith("this.");
            int start = name.IndexOf('<');
            int end = name.IndexOf('>');
            if (start >= 0 && end > start)
                name = (hasThis ? "this." : "") + name[(start + 1)..end];
        }

        // Safely strip Unity's backend static field suffixes by verifying against metadata properties
        if (typeModel != null && !name.StartsWith("this."))
        {
            if (name.EndsWith("Vector"))
            {
                string strippedName = name[..^"Vector".Length];
                if (typeModel.StaticProperties.Contains(strippedName))
                    name = strippedName;
            }
            else if (name.EndsWith("Quaternion"))
            {
                string strippedName = name[..^"Quaternion".Length];
                if (typeModel.StaticProperties.Contains(strippedName))
                    name = strippedName;
            }
        }

        return name;
    }

    /// <summary>Extract the type hint from annotation like "this.fieldName:int" → "int". Returns null if none.</summary>
    public static string? ExtractTypeHint(string? annotation)
    {
        if (annotation == null) return null;
        // Strip adjacent field encoding first
        int plusIdx = annotation.IndexOf('+');
        if (plusIdx > 0) annotation = annotation[..plusIdx];
        int colonIdx = annotation.LastIndexOf(':');
        if (colonIdx > 0 && colonIdx < annotation.Length - 1 && !annotation.Contains("::"))
            return annotation[(colonIdx + 1)..];
        return null;
    }

    /// <summary>Check if an annotation is an array element index like "[0]", "[1]", etc.
    /// These annotations should NOT be treated as field names — they should fall through
    /// to the memory offset path to generate proper arr[index] expressions.</summary>
    public static bool IsArrayElementAnnotation(string annotation)
    {
        if (annotation.Length >= 3 && annotation[0] == '[' && annotation[^1] == ']')
        {
            // Check if the content between brackets is a number
            var inner = annotation[1..^1];
            return int.TryParse(inner, out _);
        }
        return false;
    }

    /// <summary>Check if a ResultType represents a struct/value type (not a primitive or reference type).
    /// Uses negative filter: exclude known primitives and common reference types.</summary>
    public static bool IsStructResultType(string resultType)
    {
        // Exclude primitives
        return resultType switch
        {
            "System.Boolean" or "System.Byte" or "System.SByte" => false,
            "System.Int16" or "System.UInt16" or "System.Char" => false,
            "System.Int32" or "System.UInt32" => false,
            "System.Int64" or "System.UInt64" => false,
            "System.Single" or "System.Double" => false,
            "System.IntPtr" or "System.UIntPtr" => false,
            "System.String" or "System.Object" or "System.Void" => false,
            "bool" or "byte" or "sbyte" or "char" => false,
            "short" or "ushort" or "int" or "uint" => false,
            "long" or "ulong" or "float" or "double" => false,
            "string" or "object" or "void" => false,
            _ => true
        };
    }

    public static string EscapeString(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
    }
}
