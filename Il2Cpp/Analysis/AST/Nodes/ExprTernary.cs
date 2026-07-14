namespace Rosetta.Analysis.AST;

/// <summary>Ternary conditional expression (condition ? trueVal : falseVal).</summary>
public sealed class ExprTernary : ExprNode
{
    public ExprNode Condition { get; }
    public ExprNode TrueValue { get; }
    public ExprNode FalseValue { get; }

    public ExprTernary(ExprNode condition, ExprNode trueValue, ExprNode falseValue)
    {
        Condition = condition; TrueValue = trueValue; FalseValue = falseValue;
    }

    public override string Emit() => $"{Condition.Emit()} ? {TrueValue.Emit()} : {FalseValue.Emit()}";
}
