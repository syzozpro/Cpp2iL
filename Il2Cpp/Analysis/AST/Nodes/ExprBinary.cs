namespace Rosetta.Analysis.AST;

/// <summary>Binary operation (a + b, a == b, etc.).</summary>
public sealed class ExprBinary : ExprNode
{
    public string Op { get; }
    public ExprNode Left { get; }
    public ExprNode Right { get; }

    public ExprBinary(string op, ExprNode left, ExprNode right)
    {
        Op = op; Left = left; Right = right;
    }

    public override string Emit()
    {
        string left = NeedsParen(Left) ? $"({Left.Emit()})" : Left.Emit();
        string right = NeedsParen(Right) ? $"({Right.Emit()})" : Right.Emit();
        return $"{left} {Op} {right}";
    }

    private bool NeedsParen(ExprNode child)
    {
        if (child is ExprTernary) return true;
        if (child is ExprBinary childBin)
        {
            int parentPrec = GetPrecedence(Op);
            int childPrec = GetPrecedence(childBin.Op);
            // Parenthesize if child has lower precedence
            return childPrec < parentPrec;
        }
        return false;
    }

    private static int GetPrecedence(string op) => op switch
    {
        "*" or "/" or "%" => 12,
        "+" or "-" => 11,
        "<<" or ">>" => 10,
        "<" or ">" or "<=" or ">=" => 9,
        "==" or "!=" => 8,
        "&" => 7,
        "^" => 6,
        "|" => 5,
        "&&" => 4,
        "||" => 3,
        "??" => 2,
        "?:" => 1,
        _ => 0
    };
}
