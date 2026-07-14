namespace Rosetta.Analysis.AST;

/// <summary>Array element access (arr[index]).</summary>
public sealed class ExprIndex : ExprNode
{
    public ExprNode Target { get; }
    public ExprNode Index { get; }

    public ExprIndex(ExprNode target, ExprNode index)
    {
        Target = target; Index = index;
    }

    public override string Emit() => $"{Target.Emit()}[{Index.Emit()}]";
}
