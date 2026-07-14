namespace Rosetta.Analysis.AST;

/// <summary>Cast expression ((Type)expr).</summary>
public sealed class ExprCast : ExprNode
{
    public string TypeName { get; }
    public ExprNode Operand { get; }

    public ExprCast(string typeName, ExprNode operand)
    {
        TypeName = typeName; Operand = operand;
    }

    public override string Emit()
    {
        string inner = Operand.Emit();
        if (Operand is ExprBinary || Operand is ExprTernary)
            inner = $"({inner})";
        return $"({TypeName}){inner}";
    }
}
