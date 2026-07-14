namespace Rosetta.Analysis.AST;

/// <summary>Field or property access (obj.field).</summary>
public sealed class AstMemberAccess : AstExpression
{
    public AstExpression Target { get; init; } = null!;
    public string MemberName { get; init; } = "";
    public bool IsProperty { get; init; } = false;

    public override void Accept(IAstVisitor visitor) => visitor.VisitMemberAccess(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitMemberAccess(this);
}
