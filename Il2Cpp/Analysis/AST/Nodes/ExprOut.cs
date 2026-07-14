namespace Rosetta.Analysis.AST;

/// <summary>'out' parameter wrapper (e.g., out id).</summary>
public sealed class ExprOut : ExprNode
{
    public string VarName { get; }
    public ExprOut(string varName) { VarName = varName; }
    public override string Emit() => $"out {VarName}";
}
