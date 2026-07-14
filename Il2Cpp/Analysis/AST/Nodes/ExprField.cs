namespace Rosetta.Analysis.AST;

/// <summary>Field/property access (obj.Field).</summary>
public sealed class ExprField : ExprNode
{
    public ExprNode Target { get; }
    public string FieldName { get; }
    public bool IsProperty { get; }

    public ExprField(ExprNode target, string fieldName, bool isProperty = false)
    {
        Target = target; FieldName = fieldName; IsProperty = isProperty;
    }

    public override string Emit() => $"{Target.Emit()}.{FieldName}";
}
