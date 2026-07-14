namespace Rosetta.Analysis.AST;

/// <summary>If / else-if / else block.</summary>
public sealed class AstIfStatement : AstNode
{
    public AstExpression Condition { get; init; } = null!;
    public AstBlock ThenBody { get; init; } = new();
    public AstBlock? ElseBody { get; set; }

    public override void Accept(IAstVisitor visitor) => visitor.VisitIfStatement(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitIfStatement(this);
}
