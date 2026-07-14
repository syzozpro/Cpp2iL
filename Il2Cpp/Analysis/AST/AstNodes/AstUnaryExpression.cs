namespace Rosetta.Analysis.AST;

/// <summary>Unary operation (!a, -a, etc.).</summary>
public sealed class AstUnaryExpression : AstExpression
{
    public string Operator { get; init; } = "";
    public AstExpression Operand { get; init; } = null!;

    public override void Accept(IAstVisitor visitor) => visitor.VisitUnaryExpression(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitUnaryExpression(this);
}
