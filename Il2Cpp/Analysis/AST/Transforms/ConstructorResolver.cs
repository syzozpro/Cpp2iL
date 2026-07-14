using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;
using Rosetta.Analysis.Utils;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Resolves base constructors, struct constructors (stack-allocated), and heap object constructors.
/// </summary>
public static class ConstructorResolver
{
    /// <summary>
    /// Try to resolve a constructor call (ends with .ctor).
    /// </summary>
    public static ExprNode? TryResolve(IrInstruction inst, string annotation, SsaContext ssa,
        Dictionary<SsaVariable, ExprNode> exprMap, PropagationContext ctx,
        Func<SsaVariable, ExprNode> resolveVar, List<ExprNode> args,
        Func<ExprNode, bool> isThisExpr, Func<ExprNode, bool> isStackPointerExpr,
        Func<ExprNode, bool> isLocalSpVar, Func<ExprNode, long> getSpOffset)
    {
        if (ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"ConstructorResolver: TryResolve called for annotation '{annotation}', sourceCount={inst.Sources.Length}, argsCount={args.Count}");
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        .ctor handling: sources={inst.Sources.Length}");

        if (inst.Sources.Length > 1)
        {
            var targetSsa = ssa.GetSource(inst.Address, 1);
            if (targetSsa.HasValue)
            {
                var targetExpr = resolveVar(targetSsa.Value);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          ctor target: {targetExpr.Emit()} (type={targetExpr.GetType().Name})");

                if (isThisExpr(targetExpr))
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → base() call");
                    return new ExprCall("base", null, args);
                }

                if (isStackPointerExpr(targetExpr) || isLocalSpVar(targetExpr))
                {
                    string ctorType = annotation.Contains("::")
                        ? ExprUtils.CleanTypeName(annotation[..annotation.IndexOf("::")])
                        : "?";

                    var newExpr = new ExprNew(ctorType, null, new List<ExprNode>(args));
                    long spOffset = getSpOffset(targetExpr);
                    if (spOffset != 0 || targetExpr is ExprSpSlot)
                        ctx.StackSlotValues[spOffset] = newExpr;

                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → struct ctor: {targetExpr.Emit()} = new {ctorType}({string.Join(", ", args.Select(a => a.Emit()))})");
                    return new ExprAssign(targetExpr, newExpr);
                }

                // Heap object ctor: propagate args to the preceding new T() expression
                if (targetSsa.HasValue && exprMap.TryGetValue(targetSsa.Value, out var allocExpr) &&
                    allocExpr is ExprNew existingNew && existingNew.Args.Count == 0)
                {
                    if (args.Count > 0)
                    {
                        // Mutate the ExprNew in-place so already-emitted statements update
                        existingNew.Args.AddRange(args);
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → heap ctor args propagated: new {existingNew.TypeName}({string.Join(", ", existingNew.Args.Select(a => a.Emit()))})");
                    }
                    else
                    {
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → heap ctor (no args)");
                    }
                    return null;
                }
            }
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → suppress redundant .ctor");
        return null;
    }
}
