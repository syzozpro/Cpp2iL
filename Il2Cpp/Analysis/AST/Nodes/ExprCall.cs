using System.Collections.Generic;

namespace Rosetta.Analysis.AST;

/// <summary>Method call (target.Method(args) or Type.Method(args)).</summary>
public sealed class ExprCall : ExprNode
{
    public string MethodName { get; }
    public ExprNode? Target { get; set; }
    public List<ExprNode> Args { get; }

    public ExprCall(string methodName, ExprNode? target = null, List<ExprNode>? args = null)
    {
        MethodName = methodName; Target = target; Args = args ?? new();
    }

    public override string Emit()
    {
        string argsStr = string.Join(", ", Args.ConvertAll(a => a.Emit()));
        if (Target == null)
            return $"{MethodName}({argsStr})";

        // Parenthesize complex targets for correct precedence
        string targetStr = Target is ExprBinary ? $"({Target.Emit()})" : Target.Emit();
        return $"{targetStr}.{MethodName}({argsStr})";
    }
}
