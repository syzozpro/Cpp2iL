namespace Rosetta.Analysis.AST;

/// <summary>SSA variable reference (e.g., x0_1, w8_3).</summary>
public sealed class ExprVar : ExprNode
{
    public string Name { get; }
    public int VarId { get; }
    public int Version { get; }
    public byte ElementWidth { get; }
    public byte ElementCount { get; }

    public ExprVar(string name, int varId = -1, int version = -1, byte elementWidth = 0, byte elementCount = 0)
    {
        Name = name; VarId = varId; Version = version; ElementWidth = elementWidth; ElementCount = elementCount;
    }

    public override string Emit() => Name;
}
