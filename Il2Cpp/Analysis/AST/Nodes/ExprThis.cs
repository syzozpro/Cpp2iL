namespace Rosetta.Analysis.AST;

/// <summary>'this' keyword.</summary>
public sealed class ExprThis : ExprNode
{
    public override string Emit() => "this";
}
