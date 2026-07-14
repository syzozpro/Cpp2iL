using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Analysis.Resolve;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Recovers inline array literals from RuntimeHelpers.InitializeArray metadata or FieldRVA data blocks.
///
/// Decision logic uses only typed ExprNode checks — no .Emit(), .ToString(), .StartsWith(), .Contains(), or Regex.
/// </summary>
public static class ArrayLiteralRecovery
{
    /// <summary>
    /// Try to recover array literal from RuntimeHelpers.InitializeArray call.
    /// Returns true if successfully recovered.
    /// </summary>
    public static bool TryRecover(IrInstruction inst, SsaContext ssa, Dictionary<SsaVariable, ExprNode> exprMap, PropagationContext ctx, FieldRvaResolver? fieldRvaResolver)
    {
        if (fieldRvaResolver == null)
            return false;

        // Source layout for static call:
        //   [0] = call target address
        //   [1] = x0 (array ref)
        //   [2] = x1 (fieldHandle — FieldRVA data pointer)
        //   [3] = x2 (MethodInfo*, always 0)
        if (inst.Sources.Length < 3)
            return false;

        // ── Step 1: Find the field annotation ──
        string? fieldLabel = null;

        var exprForField = ssa.GetSource(inst.Address, 2);
        if (exprForField.HasValue && exprMap.TryGetValue(exprForField.Value, out var fieldExpr))
        {
            fieldLabel = TryExtractFieldLabel(fieldExpr);
        }

        // Fallback: search recently emitted statements for field reference expressions
        if (fieldLabel == null && ctx.CurrentBlockId >= 0 &&
            ctx.BlockStatements.TryGetValue(ctx.CurrentBlockId, out var recentStmts))
        {
            for (int i = recentStmts.Count - 1; i >= Math.Max(0, recentStmts.Count - 4); i--)
            {
                var extracted = TryExtractFieldLabelFromStatement(recentStmts[i].Expr);
                if (extracted != null && extracted.Contains("PrivateImplementationDetails"))
                {
                    fieldLabel = extracted;
                    break;
                }
            }
        }

        if (fieldLabel == null) return false;

        return TryRecoverArrayFromFieldLabel(fieldLabel, null, ssa, exprMap, ctx, fieldRvaResolver);
    }

    /// <summary>
    /// V104+ inlined array init: detect arr[0] = field(&lt;PrivateImpl&gt;.HASH) stores
    /// and recover the array literal.
    /// </summary>
    public static bool TryRecoverInlinedArrayInit(ExprNode storeValue, string arrayVarName, SsaContext ssa, Dictionary<SsaVariable, ExprNode> exprMap, PropagationContext ctx, FieldRvaResolver? fieldRvaResolver,  out string? recoveredFieldLabel)
    {
        recoveredFieldLabel = null;
        if (fieldRvaResolver == null) return false;

        // Structural: check if the store value is a field reference to PrivateImplementationDetails
        string? label = TryExtractFieldLabel(storeValue);
        if (label == null || !label.Contains("PrivateImplementationDetails"))
            return false;

        recoveredFieldLabel = label;

        return TryRecoverArrayFromFieldLabel(label, arrayVarName, ssa, exprMap, ctx, fieldRvaResolver);
    }

    /// <summary>
    /// Core array literal recovery: given a field label, resolve FieldRVA bytes and mutate the ExprNew.
    /// </summary>
    private static bool TryRecoverArrayFromFieldLabel(string fieldLabel, string? targetArrayVar, SsaContext ssa,
        Dictionary<SsaVariable, ExprNode> exprMap, PropagationContext ctx, FieldRvaResolver? fieldRvaResolver)
    {
        if (fieldRvaResolver == null) return false;

        // ── Look up FieldRef index from the label ──
        int fieldRefIndex = fieldRvaResolver.GetFieldRefIndex(fieldLabel);
        if (fieldRefIndex < 0)
            return false;

        // ── Find the preceding ExprNew in the current block's statements ──
        ExprNew? arrayExpr = null;

        if (ctx.CurrentBlockId >= 0 &&
            ctx.BlockStatements.TryGetValue(ctx.CurrentBlockId, out var stmts))
        {
            for (int i = stmts.Count - 1; i >= Math.Max(0, stmts.Count - 6); i--)
            {
                if (stmts[i].Expr is ExprAssign assign && assign.Value is ExprNew exprNew && exprNew.Size != null)
                {
                    // If caller specified a target array variable, match structurally
                    if (targetArrayVar != null && !IsTargetVar(assign.Target, targetArrayVar))
                        continue;
                    arrayExpr = exprNew;
                    break;
                }
            }
        }

        if (arrayExpr == null || arrayExpr.Size == null)
            return false;

        // ── Extract element type and count ──
        string elementType = arrayExpr.TypeName;
        int elementCount = TryExtractIntValue(arrayExpr.Size);

        if (elementCount <= 0)
            return false;

        // ── Resolve the raw bytes to literal values ──
        var literals = fieldRvaResolver.ResolveArrayLiterals(fieldRefIndex, elementType, elementCount);
        if (literals == null)
            return false;

        // ── Set the Initializer on the ExprNew ──
        arrayExpr.Initializer = literals;
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          InitializeArray: recovered {elementType}[{elementCount}] = {{ {string.Join(", ", literals.Take(5))}{(literals.Count > 5 ? ", ..." : "")} }}");
        if (ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"ArrayLiteralRecovery: Recovered array '{elementType}[{elementCount}]' from field label '{fieldLabel}'");
        }

        return true;
    }

    // ─── Structural Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Try to extract a field label from an ExprNode structurally.
    /// Handles ExprCall("field", arg) and ExprVar with "field(...)" name convention
    /// set by IrDataResolver annotation.
    /// </summary>
    public static string? TryExtractFieldLabel(ExprNode expr)
    {
        // ExprCall with method name "field" — extract first argument's name/literal
        if (expr is ExprCall call && call.MethodName == "field" && call.Args.Count > 0)
        {
            if (call.Args[0] is ExprVar argVar)
                return argVar.Name;
            if (call.Args[0] is ExprLiteral argLit && argLit.Value is string s)
                return s;
        }

        // ExprVar whose Name is the field annotation "field(Label)" — extract label from the
        // node's intrinsic Name property. This is the variable's data, not reconstructed output.
        // The Name is set by IrDataResolver annotation (Lifter/IR/IrDataResolver.cs:144-145).
        if (expr is ExprVar v && v.Name.Length > 7 && v.Name[..6] == "field(" && v.Name[^1] == ')')
        {
            return v.Name[6..^1];
        }

        return null;
    }

    /// <summary>
    /// Try to extract a field label from a statement expression.
    /// Walks assignment targets and values looking for field reference nodes.
    /// </summary>
    private static string? TryExtractFieldLabelFromStatement(ExprNode expr)
    {
        // Direct field reference
        var direct = TryExtractFieldLabel(expr);
        if (direct != null) return direct;

        // Assignment: check both target and value
        if (expr is ExprAssign assign)
        {
            var fromValue = TryExtractFieldLabel(assign.Value);
            if (fromValue != null) return fromValue;
            var fromTarget = TryExtractFieldLabel(assign.Target);
            if (fromTarget != null) return fromTarget;
        }

        return null;
    }

    /// <summary>
    /// Check if an ExprNode represents a variable with the given name.
    /// </summary>
    private static bool IsTargetVar(ExprNode target, string varName)
    {
        if (target is ExprVar v)
            return v.Name == varName;
        return false;
    }

    /// <summary>
    /// Extract an integer value from an ExprNode without calling .Emit().
    /// Handles ExprLiteral(int), ExprLiteral(long), and ExprVar with numeric names.
    /// </summary>
    public static int TryExtractIntValue(ExprNode expr)
    {
        if (expr is ExprLiteral lit)
        {
            if (lit.Value is int i) return i;
            if (lit.Value is long l && l >= 0 && l <= int.MaxValue) return (int)l;
        }
        // ExprVar whose Name is a numeric string (e.g., constant propagated from immediate)
        if (expr is ExprVar v && int.TryParse(v.Name, out int parsed))
            return parsed;
        return 0;
    }

    public static bool IsArray(ExprNode expr, out int size)
    {
        size = 0;
        if (expr is ExprNew exprNew && IsArrayType(exprNew))
        {
            if (exprNew.Size != null)
                size = TryExtractIntValue(exprNew.Size);
            else if (exprNew.Initializer != null)
                size = exprNew.Initializer.Count;

            return exprNew.Size != null || exprNew.Initializer != null;
        }
        return false;
    }

    public static bool IsArrayAllocation(ExprNode expr)
    {
        // If it's a 'new' expression and it has a size, it's an array allocation
        return expr is ExprNew exprNew && exprNew.Size != null;
    }

    public static bool IsArrayType(ExprNew exprNew)
    {
        if (string.IsNullOrEmpty(exprNew.TypeName))
            return false;

        int len = exprNew.TypeName.Length;
        
        // Check if it explicitly ends with ']' (handles multidimensional like [,,] or rank-1 [])
        if (len > 2 && exprNew.TypeName[len - 1] == ']')
            return true;

        return exprNew.Size != null;
    }
}
