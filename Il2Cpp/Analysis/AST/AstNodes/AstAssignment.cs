namespace Rosetta.Analysis.AST;

/// <summary>Assignment expression (lhs = rhs).</summary>
public sealed class AstAssignment : AstExpression
{
    public AstExpression Target { get; init; } = null!;
    public AstExpression Value { get; init; } = null!;

    public override void Accept(IAstVisitor visitor) => visitor.VisitAssignment(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitAssignment(this);
}
