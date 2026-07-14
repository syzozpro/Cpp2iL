using System;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

/// <summary>Type-aware value decoding: raw bits → typed ExprLiteral.</summary>
public sealed partial class ExprPropagator
{


    private static object FormatImm(long val, byte bitWidth)
    {
        var result = bitWidth switch
        {
            8 => (object)(byte)val, 16 => (short)val,
            32 => FormatImm32(val),
            // 64 when val == 0 => null!,
            _ => val  // Always store as long — downstream folding handles simplification
        };
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        FormatImm(val=0x{val:X}, bits={bitWidth}) → {result}");
        return result;
    }

    /// <summary>Format a 32-bit immediate, decoding IEEE 754 floats when appropriate.</summary>
    private static object FormatImm32(long val)
    {
        if (val >= -1024 && val <= 65535)
            return (int)val;
        // Check if this looks like an IEEE 754 float bit pattern
        uint bits = (uint)(val & 0xFFFFFFFF);
        uint exponent = (bits >> 23) & 0xFF;
        // Valid float exponents are 1..254 (0 = denorm/zero, 255 = inf/NaN)
        if (exponent >= 1 && exponent <= 254)
        {
            float f = BitConverter.Int32BitsToSingle((int)bits);
            if (!float.IsNaN(f) && !float.IsInfinity(f))
                return f;
        }
        // Return as numeric value, not hex string
        return (int)(val & 0xFFFFFFFF);
    }

    private static object FormatFloat(long val, byte bitWidth)
    {
        object result;
        if (bitWidth <= 32)
        {
            float f = BitConverter.Int32BitsToSingle((int)val);
            result = f;
        }
        else
        {
            double d = BitConverter.Int64BitsToDouble(val);
            result = d;
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        FormatFloat(val=0x{val:X}, bits={bitWidth}) → {result}");
        return result;
    }
}
