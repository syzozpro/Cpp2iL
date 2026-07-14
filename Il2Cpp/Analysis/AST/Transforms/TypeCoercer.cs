using System;
using System.Collections.Generic;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Model;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Universal metadata-driven parameter type coercion.
/// Look up the target method's parameter types from the type model.
/// When the argument is a literal whose representation differs from the declared parameter type,
/// coerce it to the correct C# type.
/// </summary>
public static class TypeCoercer
{
    public static void Coerce(List<ExprNode> args, IReadOnlyList<MethodSignature.ParamEntry>? parameters)
    {
        if (parameters != null)
        {
            if (ConsoleReporter.Verbose)
            {
                ConsoleReporter.Debug($"TypeCoercer: Coercing {args.Count} args");
            }
            for (int i = 0; i < args.Count && i < parameters.Count; i++)
            {
                string paramType = parameters[i].TypeName;

                if (args[i] is ExprOut exprOut)
                {
                    if (paramType.StartsWith("ref "))
                        args[i] = new ExprRef(exprOut.VarName);
                    else if (paramType.StartsWith("in "))
                        args[i] = new ExprIn(exprOut.VarName);
                }

                if (args[i] is not ExprLiteral lit) continue;

                args[i] = CoerceLiteralToType(lit, paramType);
            }
        }
    }

    private static ExprNode CoerceLiteralToType(ExprLiteral lit, string paramType)
    {
        long? intVal = lit.Value switch
        {
            int i => i,
            long l => l,
            _ => null
        };

        // Only coerce integer literals — floats, strings, nulls, bools are already correct
        if (intVal == null) return lit;
        long v = intVal.Value;

        var coerced = paramType switch
        {
            // Boolean: 0 → false, non-zero → true
            "System.Boolean" or "bool" =>
                (ExprNode)new ExprLiteral(v != 0),

            // Single-precision float: reinterpret 32-bit integer as IEEE 754 float
            "System.Single" or "float" when v != 0 && (v < -1000 || v > 1000 || IsLikelyBitPattern(v)) =>
                new ExprLiteral(BitConverter.Int32BitsToSingle((int)(v & 0xFFFFFFFF))),

            // Double-precision float: reinterpret 64-bit integer as IEEE 754 double
            "System.Double" or "double" when v != 0 && lit.Value is long =>
                new ExprLiteral(BitConverter.Int64BitsToDouble(v)),

            // Character: integer → char literal
            "System.Char" or "char" when v >= 0 && v <= char.MaxValue =>
                new ExprLiteral((char)v),

            // Narrowing integer conversions — preserve the value at the correct width
            "System.Byte" or "byte" when v >= byte.MinValue && v <= byte.MaxValue =>
                new ExprCast("byte", lit),
            "System.SByte" or "sbyte" when v >= sbyte.MinValue && v <= sbyte.MaxValue =>
                new ExprCast("sbyte", lit),
            "System.Int16" or "short" when v >= short.MinValue && v <= short.MaxValue =>
                new ExprCast("short", lit),
            "System.UInt16" or "ushort" when v >= ushort.MinValue && v <= ushort.MaxValue =>
                new ExprCast("ushort", lit),

            // No coercion needed — type already matches or is int/long/object
            _ => lit
        };

        if (coerced != lit && ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"TypeCoercer: Coerced literal '{lit.Value}' to type '{paramType}' -> '{coerced.Emit()}'");
        }
        return coerced;
    }

    /// <summary>
    /// Check if an integer value looks like an IEEE 754 bit pattern rather than a
    /// normal integer. ARM64 compilers load constant-folded floats via MOV into
    /// integer registers (e.g., 0x3F800000 = 1.0f).
    /// This checks the exponent field: if it's in the typical float range, it's a bit pattern.
    /// </summary>
    private static bool IsLikelyBitPattern(long v)
    {
        if (v < 0) return false;
        uint bits = (uint)(v & 0xFFFFFFFF);
        // Extract IEEE 754 single-precision exponent (bits 23-30)
        int exponent = (int)((bits >> 23) & 0xFF);
        // Valid float exponents for "normal" values are 1-254 (0 = denorm, 255 = inf/nan)
        // Typical game floats have exponents in range ~100-160 (values roughly 0.001 to 100000)
        return exponent >= 1 && exponent <= 254 && bits > 0x00800000;
    }
}
