namespace Rosetta.Analysis.AST;

/// <summary>Array element access (arr[index]).</summary>
public sealed class AstIndexAccess : AstExpression
{
    public AstExpression Target { get; init; } = null!;
    public AstExpression Index { get; init; } = null!;

    public override void Accept(IAstVisitor visitor) => visitor.VisitIndexAccess(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitIndexAccess(this);
}
