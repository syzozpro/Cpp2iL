namespace Rosetta.Analysis.AST;

/// <summary>typeof(T) expression.</summary>
public sealed class ExprTypeOf : ExprNode
{
    public string TypeName { get; }
    public ExprTypeOf(string typeName) { TypeName = typeName; }
    public override string Emit() => $"typeof({TypeName})";
}
