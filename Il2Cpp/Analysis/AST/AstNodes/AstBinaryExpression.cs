namespace Rosetta.Analysis.AST;

/// <summary>Binary operation (a + b, a == b, etc.).</summary>
public sealed class AstBinaryExpression : AstExpression
{
    public string Operator { get; init; } = "";
    public AstExpression Left { get; init; } = null!;
    public AstExpression Right { get; init; } = null!;

    public override void Accept(IAstVisitor visitor) => visitor.VisitBinaryExpression(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitBinaryExpression(this);
}
