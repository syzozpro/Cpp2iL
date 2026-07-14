using System;
using System.Collections.Generic;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;
using Rosetta.Analysis.AST;

namespace Rosetta.Analysis.Utils;

/// <summary>Pure functional static predicates and helpers for evaluating AST expressions.</summary>
public static class ExprUtils
{
    public static bool IsThisExpr(ExprNode expr)
        => expr is ExprThis || (expr is ExprVar v && v.Name == "this");

    public static bool IsLocalSpVar(ExprNode expr)
        => expr is ExprSpSlot;

    public static bool IsStringLiteralExpr(ExprNode expr, Dictionary<SsaVariable, ExprNode> exprMap)
    {
        if (expr is ExprLiteral lit && lit.Value is string) return true;
        // Check if variable was assigned a string literal in ExprMap
        if (expr is ExprVar v && v.VarId >= 0 && v.Version >= 0)
        {
            // Search ExprMap for a matching SsaVariable key
            foreach (var kv in exprMap)
            {
                if (kv.Key.VarId == v.VarId && kv.Key.Version == v.Version &&
                    kv.Value is ExprLiteral mLit && mLit.Value is string)
                    return true;
            }
        }
        return false;
    }

    /// <summary>Check if expression is SP + offset (stack pointer expression for out/ref).</summary>
    public static bool IsStackPointerExpr(ExprNode expr)
    {
        if (expr is ExprBinary bin && bin.Op == "+" &&
            bin.Left is ExprVar spVar && spVar.Name == "SP")
            return true;
        if (expr is ExprSpSlot)
            return true;
        if (expr is ExprVar v && v.Name == "SP")
            return true;
        return false;
    }

    /// <summary>Extract the offset from a stack pointer expression.</summary>
    public static long GetSpOffset(ExprNode expr)
    {
        if (expr is ExprBinary bin && bin.Right is ExprLiteral lit)
        {
            if (lit.Value is int i) return i;
            if (lit.Value is long l) return l;
        }
        if (expr is ExprSpSlot slot) return slot.Offset;
        return 0;
    }

    /// <summary>Try to extract SP slot offset. Returns null if not a stack pointer expression.</summary>
    public static long? TryGetSpSlotOffset(ExprNode expr)
    {
        if (expr is ExprSpSlot slot) return slot.Offset;
        if (expr is ExprBinary bin && bin.Op == "+" &&
            bin.Left is ExprVar sp && sp.Name == "SP" &&
            bin.Right is ExprLiteral lit)
        {
            if (lit.Value is int i) return i;
            if (lit.Value is long l) return l;
        }
        return null;
    }

    public static bool IsSameType(string? typeName, string? declaringType)
        => declaringType != null && typeName != null &&
           (declaringType == typeName || declaringType.EndsWith("." + typeName));

    public static bool IsStaticMethod(string annotation)
    {
        if (annotation.Contains("il2cpp_runtime") || annotation.Contains("class_init") ||
            annotation.Contains("post_call") || annotation.Contains("op_"))
            return true;
        return false;
    }

    public static bool IsNullOrZeroLiteral(ExprNode expr)
    {
        if (expr is ExprLiteral lit)
        {
            if (lit.Value == null) return true;
            if (lit.Value is int i && i == 0) return true;
            if (lit.Value is long l && l == 0) return true;
        }
        return false;
    }

    /// <summary>Check if expression is a MethodRef annotation (hidden MethodInfo* arg).</summary>
    public static bool IsMethodRefExpr(ExprNode expr)
        => expr is ExprMethodRef;

    public static bool IsFloatLiteral(ExprNode node)
    {
        // Primary: typed ExprLiteral with float value
        if (node is ExprLiteral lit && lit.Value is float)
            return true;
        // Legacy fallback: ExprVar name like "3.14f" (pre-refactor data)
        if (node is ExprVar v && v.Name.Length > 1 && v.Name.EndsWith("f"))
        {
            return float.TryParse(v.Name[..^1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _);
        }
        return false;
    }

    public static bool IsDoubleLiteral(ExprNode node)
    {
        // Primary: typed ExprLiteral with double value
        if (node is ExprLiteral lit && lit.Value is double)
            return true;
        // Legacy fallback: ExprVar name like "2.5d" (pre-refactor data)
        if (node is ExprVar v && v.Name.Length > 1 && v.Name.EndsWith("d"))
        {
            return double.TryParse(v.Name[..^1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _);
        }
        return false;
    }

    // In SSA form, every variable is assigned exactly once.
    public static bool IsFirstDefinition(SsaVariable v) => true;

    /// <summary>Check if expression is a constructor result (updated by .ctor call).</summary>
    public static bool IsConstructorResult(ExprNode expr)
    {
        if (expr is ExprNew) return true;
        if (expr is ExprAssign { Value: ExprNew }) return true;
        return false;
    }

    /// <summary>
    /// When boxing as float/double, reinterpret hex integer literals as IEEE 754 floats.
    /// ARM64 compilers often load constant-folded floats via integer registers (MOV w21, #0x3F3504F3).
    /// </summary>
    public static ExprNode TryReinterpretAsFloat(ExprNode expr, string boxType)
    {
        if (boxType is not ("float" or "double" or "Single" or "Double")) return expr;

        string? hexStr = null;
        if (expr is ExprLiteral lit && lit.Value is string s && s.StartsWith("0x"))
            hexStr = s;
        else if (expr is ExprVar v && v.Name.StartsWith("0x"))
            hexStr = v.Name;

        if (hexStr == null) return expr;

        if (!long.TryParse(hexStr[2..], System.Globalization.NumberStyles.HexNumber, null, out long bits))
            return expr;

        if (boxType is "float" or "Single")
        {
            float f = BitConverter.Int32BitsToSingle((int)bits);
            if (!float.IsNaN(f) && !float.IsInfinity(f))
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          reinterpret hex→float: {hexStr} → {f}f");
                return new ExprLiteral(f);
            }
        }
        else
        {
            double d = BitConverter.Int64BitsToDouble(bits);
            if (!double.IsNaN(d) && !double.IsInfinity(d))
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          reinterpret hex→double: {hexStr} → {d}d");
                return new ExprLiteral(d);
            }
        }

        return expr;
    }

    /// <summary>
    /// Check if a type name is a primitive value type whose "new T()" annotation
    /// actually represents a boxing operation (il2cpp_object_new), not a heap allocation.
    /// </summary>
    public static bool IsPrimitiveBoxType(string typeName) => typeName switch
    {
        "int" or "Int32" or "uint" or "UInt32" => true,
        "long" or "Int64" or "ulong" or "UInt64" => true,
        "short" or "Int16" or "ushort" or "UInt16" => true,
        "byte" or "Byte" or "sbyte" or "SByte" => true,
        "float" or "Single" or "double" or "Double" => true,
        "bool" or "Boolean" => true,
        "char" or "Char" => true,
        _ => false
    };

    /// <summary>
    /// Clean a type name using ToAlias.
    /// </summary>
    public static string CleanTypeName(string raw) => Rosetta.Common.TypeUtils.ToAlias(raw);

    /// <summary>
    /// Check if a register (by its SSA VarId) is a callee-saved register in AArch64.
    /// GP registers (X19-X30) are 19-30. FP registers (D8-D15) are 108-115.
    /// </summary>
    public static bool IsCalleeSavedRegister(int varId)
    {
        if (varId >= 19 && varId <= 30) return true;
        if (varId >= 108 && varId <= 115) return true;
        return false;
    }
}
