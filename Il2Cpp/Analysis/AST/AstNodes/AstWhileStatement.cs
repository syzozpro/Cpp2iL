namespace Rosetta.Analysis.AST;

/// <summary>While loop.</summary>
public sealed class AstWhileStatement : AstNode
{
    public AstExpression Condition { get; init; } = null!;
    public AstBlock Body { get; init; } = new();

    public override void Accept(IAstVisitor visitor) => visitor.VisitWhileStatement(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitWhileStatement(this);
}
