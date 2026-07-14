namespace Rosetta.Analysis.AST;

/// <summary>Foreach loop (foreach var item in collection).</summary>
public sealed class AstForeachStatement : AstNode
{
    public string ItemName { get; set; } = "item";
    public string CollectionName { get; set; } = "collection";
    public AstBlock Body { get; init; } = new();

    public override void Accept(IAstVisitor visitor) => visitor.VisitForeachStatement(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitForeachStatement(this);
}
