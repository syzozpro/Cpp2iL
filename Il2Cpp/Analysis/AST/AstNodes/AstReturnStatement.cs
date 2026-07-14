namespace Rosetta.Analysis.AST;

/// <summary>Return statement.</summary>
public sealed class AstReturnStatement : AstNode
{
    public AstExpression? Value { get; set; }

    public override void Accept(IAstVisitor visitor) => visitor.VisitReturnStatement(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitReturnStatement(this);
}
