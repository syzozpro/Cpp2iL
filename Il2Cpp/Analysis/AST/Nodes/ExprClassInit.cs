namespace Rosetta.Analysis.AST;

/// <summary>Class initialization flag check.</summary>
public sealed class ExprClassInit : ExprNode
{
    public string TypeName { get; }

    public ExprClassInit(string typeName)
    {
        TypeName = typeName;
    }

    public override string Emit() => $"class_init_flag<{TypeName}>";
}
