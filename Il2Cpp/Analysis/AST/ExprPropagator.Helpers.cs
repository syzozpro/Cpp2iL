using Rosetta.Analysis.IR.SSA;
using Rosetta.Analysis.Utils;

namespace Rosetta.Analysis.AST;

public sealed partial class ExprPropagator
{
    private bool IsThisExpr(ExprNode expr) => ExprUtils.IsThisExpr(expr);
    private static bool IsLocalSpVar(ExprNode expr) => ExprUtils.IsLocalSpVar(expr);
    private bool IsStringLiteralExpr(ExprNode expr) => ExprUtils.IsStringLiteralExpr(expr, ExprMap);
    private static bool IsStackPointerExpr(ExprNode expr) => ExprUtils.IsStackPointerExpr(expr);
    private static long GetSpOffset(ExprNode expr) => ExprUtils.GetSpOffset(expr);
    private bool IsSameType(string? typeName) => ExprUtils.IsSameType(typeName, _declaringType);
    private static bool IsStaticMethod(string annotation) => ExprUtils.IsStaticMethod(annotation);
    private static bool IsNullOrZeroLiteral(ExprNode expr) => ExprUtils.IsNullOrZeroLiteral(expr);
    private static bool IsMethodRefExpr(ExprNode expr) => ExprUtils.IsMethodRefExpr(expr);
    private static bool IsFloatLiteral(ExprNode node) => ExprUtils.IsFloatLiteral(node);
    private bool IsFirstDefinition(SsaVariable v) => ExprUtils.IsFirstDefinition(v);
    private static bool IsConstructorResult(ExprNode expr) => ExprUtils.IsConstructorResult(expr);
    private static ExprNode TryReinterpretAsFloat(ExprNode expr, string boxType) => ExprUtils.TryReinterpretAsFloat(expr, boxType);
    private static bool IsPrimitiveBoxType(string typeName) => ExprUtils.IsPrimitiveBoxType(typeName);

    public string GetVarName(SsaVariable v)
    {
        var expr = MakeVarExpr(v);
        return expr is ExprVar ev ? ev.Name : expr.Emit();
    }

    private void LogPropagationSummary()
    {
        if (!Rosetta.Pipeline.ConsoleReporter.IsTracing)
            return;

        int totalStmts = 0;
        foreach (var s in BlockStatements.Values)
            totalStmts += s.Count;

        Rosetta.Pipeline.ConsoleReporter.Trace($"Propagate() END: {ExprMap.Count} exprs, {Inlined.Count} inlined, {totalStmts} total statements");
    }
}
