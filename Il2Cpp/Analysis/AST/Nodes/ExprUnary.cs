namespace Rosetta.Analysis.AST;

/// <summary>Unary operation (!a, -a).</summary>
public sealed class ExprUnary : ExprNode
{
    public string Op { get; }
    public ExprNode Operand { get; }

    public ExprUnary(string op, ExprNode operand) { Op = op; Operand = operand; }
    public override string Emit()
    {
        string inner = Operand.Emit();
        // Parenthesize complex operands for correct precedence
        // e.g., (float)(a + b) instead of (float)a + b
        if (Operand is ExprBinary)
            inner = $"({inner})";
        return $"{Op}{inner}";
    }
}
