namespace Rosetta.Analysis.AST;

/// <summary>Type cast ((Type)expr).</summary>
public sealed class AstCastExpression : AstExpression
{
    public string TypeName { get; init; } = "";
    public AstExpression Operand { get; init; } = null!;

    public override void Accept(IAstVisitor visitor) => visitor.VisitCastExpression(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitCastExpression(this);
}
