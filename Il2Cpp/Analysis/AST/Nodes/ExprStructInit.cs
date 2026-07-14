using System.Collections.Generic;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Struct initializer expression: new TypeName { field1 = val1, field2 = val2, ... }
/// Used for stack-allocated value types whose fields were initialized via stores.
/// </summary>
public sealed class ExprStructInit : ExprNode
{
    public string TypeName { get; }

    /// <summary>Ordered field initializers (fieldName → value expression).</summary>
    public List<(string FieldName, ExprNode Value)> Fields { get; } = new();

    public ExprStructInit(string typeName)
    {
        TypeName = typeName;
    }

    public override string Emit()
    {
        if (Fields.Count == 0)
            return $"new {TypeName}()";

        var parts = new List<string>(Fields.Count);
        foreach (var (name, val) in Fields)
            parts.Add($"{name} = {val.Emit()}");

        return $"new {TypeName} {{ {string.Join(", ", parts)} }}";
    }
}
