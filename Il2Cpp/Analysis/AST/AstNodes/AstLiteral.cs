namespace Rosetta.Analysis.AST;

/// <summary>A literal constant (int, float, string, null, bool).</summary>
public sealed class AstLiteral : AstExpression
{
    public object? Value { get; init; }
    public override string ToString() => Value?.ToString() ?? "null";

    public override void Accept(IAstVisitor visitor) => visitor.VisitLiteral(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitLiteral(this);
}
