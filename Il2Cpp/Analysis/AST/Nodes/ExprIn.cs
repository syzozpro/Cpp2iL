namespace Rosetta.Analysis.AST;

/// <summary>'in' parameter wrapper (e.g., in id).</summary>
public sealed class ExprIn : ExprNode
{
    public string VarName { get; }
    public ExprIn(string varName) { VarName = varName; }
    public override string Emit() => $"in {VarName}";
}
