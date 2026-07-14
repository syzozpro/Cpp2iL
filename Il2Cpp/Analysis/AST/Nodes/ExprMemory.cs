namespace Rosetta.Analysis.AST;

/// <summary>Raw memory dereference — fallback for unresolved accesses.</summary>
public sealed class ExprMemory : ExprNode
{
    public ExprNode Base { get; }
    public long Offset { get; }

    public ExprMemory(ExprNode baseExpr, long offset)
    {
        Base = baseExpr; Offset = offset;
    }

    public override string Emit()
    {
        if (Offset == 0) return $"*({Base.Emit()})";
        if (Offset < 0) return $"*([{Base.Emit()} - 0x{-Offset:X}])";
        return $"*([{Base.Emit()} + 0x{Offset:X}])";
    }
}
