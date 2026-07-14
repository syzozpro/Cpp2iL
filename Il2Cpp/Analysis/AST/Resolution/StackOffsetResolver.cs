using System;

namespace Rosetta.Analysis.AST;

/// <summary>Resolves AST expressions that denote SP-relative storage offsets.</summary>
public static class StackOffsetResolver
{
    public static bool TryGetOffset(ExprNode expr, out long offset)
    {
        if (expr is ExprSpSlot slot)
        {
            offset = slot.Offset;
            return true;
        }

        if (expr is ExprBinary { Left: ExprSpSlot baseSlot, Right: ExprLiteral lit } bin &&
            TryGetIntegerLiteral(lit.Value, out long delta))
        {
            if (bin.Op == "+")
            {
                offset = baseSlot.Offset + delta;
                return true;
            }

            if (bin.Op == "-")
            {
                offset = baseSlot.Offset - delta;
                return true;
            }
        }

        if (expr is ExprBinary { Op: "+", Left: ExprVar { Name: "SP" }, Right: ExprLiteral spLit } &&
            TryGetSpLiteral(spLit.Value, out long spOffset))
        {
            offset = spOffset;
            return true;
        }

        offset = 0;
        return false;
    }

    private static bool TryGetSpLiteral(object? value, out long result)
    {
        if (TryGetIntegerLiteral(value, out result))
            return true;

        if (value is string s)
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out result);

            return long.TryParse(s, out result);
        }

        if (value is IConvertible convertible)
        {
            try
            {
                result = convertible.ToInt64(null);
                return true;
            }
            catch
            {
                result = 0;
                return false;
            }
        }

        result = 0;
        return false;
    }

    private static bool TryGetIntegerLiteral(object? value, out long result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
