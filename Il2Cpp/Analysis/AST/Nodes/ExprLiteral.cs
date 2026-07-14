namespace Rosetta.Analysis.AST;

/// <summary>Literal constant (int, float, string, null, bool).</summary>
public sealed class ExprLiteral : ExprNode
{
    public object? Value { get; }
    public ExprLiteral(object? value) { Value = value; }
    public override string Emit() => Value switch
    {
        null => "null",
        string s => $"\"{Rosetta.Analysis.Utils.StringUtils.EscapeString(s)}\"",
        bool b => b ? "true" : "false",
        float f => Rosetta.Common.TypeUtils.FormatFloat(f),
        double d => Rosetta.Common.TypeUtils.FormatDouble(d) + "d",
        long l => (l >= int.MinValue && l <= int.MaxValue) ? l.ToString() : l.ToString() + "L",
        ulong ul => ul.ToString() + "ul",
        uint ui => ui.ToString() + "u",
        _ => Value.ToString() ?? "?"
    };
}
