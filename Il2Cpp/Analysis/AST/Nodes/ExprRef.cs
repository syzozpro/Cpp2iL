namespace Rosetta.Analysis.AST;

/// <summary>'ref' parameter wrapper (e.g., ref id).</summary>
public sealed class ExprRef : ExprNode
{
    public string VarName { get; }
    public ExprRef(string varName) { VarName = varName; }
    public override string Emit() => $"ref {VarName}";
}
