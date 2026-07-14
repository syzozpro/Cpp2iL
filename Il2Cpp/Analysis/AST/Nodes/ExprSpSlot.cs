namespace Rosetta.Analysis.AST;

/// <summary>SP-relative memory slot (for stack spills).</summary>
public sealed class ExprSpSlot : ExprNode
{
    public long Offset { get; }
    public ExprSpSlot(long offset) { Offset = offset; }
    public override string Emit() => $"local_sp{Offset:X}";
}
