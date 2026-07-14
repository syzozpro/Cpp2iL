namespace Rosetta.Analysis.AST;

/// <summary>For loop.</summary>
public sealed class AstForStatement : AstNode
{
    public AstNode? Init { get; set; }
    public AstExpression? Condition { get; set; }
    public AstNode? Update { get; set; }
    public AstBlock Body { get; init; } = new();

    public override void Accept(IAstVisitor visitor) => visitor.VisitForStatement(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitForStatement(this);
}
