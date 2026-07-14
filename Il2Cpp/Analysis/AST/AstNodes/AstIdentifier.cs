using Rosetta.Analysis.IR.SSA;

namespace Rosetta.Analysis.AST;

/// <summary>A variable or register reference.</summary>
public sealed class AstIdentifier : AstExpression
{
    public string Name { get; set; } = "";
    public SsaVariable? SsaVar { get; set; }
    public override string ToString() => Name;

    public override void Accept(IAstVisitor visitor) => visitor.VisitIdentifier(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitIdentifier(this);
}
