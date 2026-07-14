namespace Rosetta.Analysis.AST;

/// <summary>Helpers for IL2CPP array address expressions.</summary>
public static class ArrayAddressResolver
{
    public const int Il2CppArrayDataOffset = 32;

    public static ExprNode StripArrayDataOffset(ExprNode expr)
    {
        if (expr is ExprBinary { Op: "+", Right: ExprLiteral lit } bin &&
            lit.Value is int or long &&
            ConvertLiteralToLong(lit.Value) == Il2CppArrayDataOffset)
        {
            return bin.Left;
        }

        return expr;
    }

    private static long ConvertLiteralToLong(object value)
        => value is int i ? i : (long)value;
}
