namespace Rosetta.Analysis.AST;

/// <summary>Assignment expression (target = value).</summary>
public sealed class ExprAssign : ExprNode
{
    public ExprNode Target { get; }
    public ExprNode Value { get; }

    public ExprAssign(ExprNode target, ExprNode value)
    {
        Target = target; Value = value;
    }

    public override string Emit() => $"{Target.Emit()} = {Value.Emit()}";
}
