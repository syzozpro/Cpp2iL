namespace Rosetta.Analysis.AST;

/// <summary>VTable method access (vtable[slot]).</summary>
public sealed class ExprVTableAccess : ExprNode
{
    public string TypeName { get; }
    public int SlotIndex { get; }

    public ExprVTableAccess(string typeName, int slotIndex)
    {
        TypeName = typeName; 
        SlotIndex = slotIndex;
    }

    public override string Emit() => $"vtable<{TypeName}>[{SlotIndex}]";
}
