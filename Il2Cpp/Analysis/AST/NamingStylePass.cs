using System;
using System.Collections.Generic;
using System.Linq;

namespace Rosetta.Analysis.AST;

public static class NamingStylePass
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this",
        "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while", "var"
    };

    private static bool IsVowel(char c)
    {
        c = char.ToLowerInvariant(c);
        return c == 'a' || c == 'e' || c == 'i' || c == 'o' || c == 'u' || c == 'y';
    }

    private static List<string> SplitPascalCase(string name)
    {
        var words = new List<string>();
        int start = 0;
        for (int i = 1; i < name.Length; i++)
        {
            bool isUpper = char.IsUpper(name[i]);
            bool prevUpper = char.IsUpper(name[i - 1]);
            bool isDigit = char.IsDigit(name[i]);
            bool prevDigit = char.IsDigit(name[i - 1]);

            if ((isUpper && !prevUpper) || (isDigit && !prevDigit) || (!isDigit && prevDigit))
            {
                words.Add(name[start..i]);
                start = i;
            }
            else if (isUpper && prevUpper && i + 1 < name.Length && char.IsLower(name[i + 1]))
            {
                words.Add(name[start..i]);
                start = i;
            }
        }
        if (start < name.Length)
        {
            words.Add(name[start..]);
        }
        return words;
    }

    private static string GetPrefixForType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName) || typeName == "var") return "val";

        // Strip namespace
        int lastDot = typeName.LastIndexOf('.');
        string name = lastDot >= 0 ? typeName[(lastDot + 1)..] : typeName;

        // Strip array brackets/generics
        int bracket = name.IndexOf('[');
        if (bracket >= 0) name = name[..bracket] + "Array";
        int angle = name.IndexOf('<');
        if (angle >= 0) name = name[..angle];

        string prefix;
        if (name == "string" || name == "String") prefix = "strr";
        else if (name == "int" || name == "Int32" || name == "uint" || name == "UInt32") prefix = "num";
        else if (name == "long" || name == "Int64" || name == "ulong" || name == "UInt64") prefix = "num";
        else if (name == "short" || name == "Int16" || name == "ushort" || name == "UInt16" || name == "byte" || name == "sbyte") prefix = "num";
        else if (name == "float" || name == "Single" || name == "double" || name == "Double" || name == "decimal" || name == "Decimal") prefix = "val";
        else if (name == "bool" || name == "Boolean") prefix = "flag";
        else if (name.Length > 0)
        {
            var words = SplitPascalCase(name);
            if (words.Count >= 2)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var w in words)
                {
                    if (w.Length > 0) sb.Append(char.ToLowerInvariant(w[0]));
                }
                prefix = sb.ToString();
            }
            else
            {
                string w = words[0];
                if (w.Length <= 4)
                {
                    prefix = w.ToLowerInvariant();
                }
                else
                {
                    int firstVowel = -1;
                    for (int i = 0; i < w.Length; i++)
                    {
                        if (IsVowel(w[i]))
                        {
                            firstVowel = i;
                            break;
                        }
                    }

                    if (firstVowel == -1)
                    {
                        prefix = w.ToLowerInvariant();
                    }
                    else
                    {
                        int targetLen = Math.Clamp(firstVowel + 3, 3, w.Length);
                        string sub = w[..targetLen].ToLowerInvariant();
                        if (sub.Length > 3 && IsVowel(sub[^1]))
                        {
                            sub = sub[..^1];
                        }
                        prefix = sub;
                    }
                }
            }
        }
        else
        {
            prefix = "obj";
        }

        if (CSharpKeywords.Contains(prefix))
        {
            return prefix switch
            {
                "object" => "obj",
                "class" => "cls",
                "struct" => "strct",
                "interface" => "iface",
                "event" => "evt",
                "void" => "val",
                "delegate" => "del",
                "var" => "val",
                _ => prefix + "_"
            };
        }
        return prefix;
    }

    public static void ApplyNamingStyle(AstMethod method)
    {
        var gatherer = new AstDeclarationGatherer();
        method.Body.Accept(gatherer);

        var prefixCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var nameMapping = new Dictionary<string, string>(StringComparer.Ordinal);

        // Seed with parameter names to avoid parameter shadowing
        foreach (var p in method.Parameters)
        {
            string[] parts = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                string pName = parts[^1];
                prefixCounts[pName] = 1;
            }
        }

        // Also seed standard C# keywords/builtins just to be safe
        prefixCounts["this"] = 1;
        prefixCounts["value"] = 1;

        // Assign new names based on type
        foreach (var decl in gatherer.Declarations)
        {
            string oldName = decl.VarName;
            if (string.IsNullOrEmpty(oldName) || oldName == "this" || oldName == "value") continue;

            // Only rename register/local temporaries (e.g. x0_12, d0_6, w19_3, local_spX, hfa_ret_X)
            bool isRegister = (oldName.StartsWith("x") || oldName.StartsWith("w") || oldName.StartsWith("R") ||
                               oldName.StartsWith("s") || oldName.StartsWith("d") || oldName.StartsWith("q") ||
                               oldName.StartsWith("v") || oldName.StartsWith("local_sp") || oldName.StartsWith("hfa_ret_")) && 
                              (oldName.Contains('_') || oldName.Length > 2);
            if (!isRegister) continue;

            if (nameMapping.ContainsKey(oldName)) continue;

            string prefix = GetPrefixForType(decl.TypeName);
            if (!prefixCounts.TryGetValue(prefix, out var count))
            {
                nameMapping[oldName] = prefix;
                prefixCounts[prefix] = 1;
            }
            else
            {
                nameMapping[oldName] = $"{prefix}{count}";
                prefixCounts[prefix] = count + 1;
            }
        }

        if (nameMapping.Count == 0) return;

        // Apply renaming to AST
        var renamer = new AstVariableRenamer(nameMapping);
        method.Body.Accept(renamer);

        // Update OutVariableDeclarations
        var newOuts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var oldOut in method.OutVariableDeclarations)
        {
            if (nameMapping.TryGetValue(oldOut, out var newOut))
                newOuts.Add(newOut);
            else
                newOuts.Add(oldOut);
        }
        method.OutVariableDeclarations.Clear();
        foreach (var o in newOuts)
            method.OutVariableDeclarations.Add(o);
    }
}
