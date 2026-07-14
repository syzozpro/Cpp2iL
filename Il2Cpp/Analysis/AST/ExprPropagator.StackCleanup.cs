using System.Collections.Generic;
using System.Linq;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

public sealed partial class ExprPropagator
{
    private void RemoveDeadStackStores()
    {
        var usage = CollectStackUsage();
        if (usage.SpAddressTaken)
            return;

        foreach (var (blockId, stmts) in BlockStatements)
        {
            int before = stmts.Count;
            stmts.RemoveAll(stmt =>
                stmt.Expr is ExprAssign assign &&
                assign.Target is ExprSpSlot slot &&
                !usage.UsedSpOffsets.Contains(slot.Offset));
            int removed = before - stmts.Count;
            if (removed > 0 && ConsoleReporter.IsTracing)
                ConsoleReporter.Debug($"    block {blockId}: removed {removed} completely dead SP stores");
        }
    }

    private StackUsage CollectStackUsage()
    {
        var usedSpOffsets = new HashSet<long>();
        bool spAddressTaken = false;

        foreach (var stmt in BlockStatements.Values.SelectMany(stmts => stmts))
            MarkStackUsage(stmt.Expr, usedSpOffsets, ref spAddressTaken);

        return new StackUsage(usedSpOffsets, spAddressTaken);
    }

    private static void MarkStackUsage(ExprNode? expr, HashSet<long> usedSpOffsets, ref bool spAddressTaken)
    {
        switch (expr)
        {
            case null:
                return;
            case ExprSpSlot spSlot:
                usedSpOffsets.Add(spSlot.Offset);
                return;
            case ExprVar { Name: "SP" }:
                spAddressTaken = true;
                return;
            case ExprAssign assign:
                MarkStackUsage(assign.Value, usedSpOffsets, ref spAddressTaken);
                return;
            case ExprBinary bin:
                MarkStackUsage(bin.Left, usedSpOffsets, ref spAddressTaken);
                MarkStackUsage(bin.Right, usedSpOffsets, ref spAddressTaken);
                return;
            case ExprUnary un:
                MarkStackUsage(un.Operand, usedSpOffsets, ref spAddressTaken);
                return;
            case ExprCall call:
                MarkStackUsage(call.Target, usedSpOffsets, ref spAddressTaken);
                foreach (var arg in call.Args)
                    MarkStackUsage(arg, usedSpOffsets, ref spAddressTaken);
                return;
            case ExprMemory mem:
                MarkStackUsage(mem.Base, usedSpOffsets, ref spAddressTaken);
                return;
            case ExprCast cast:
                MarkStackUsage(cast.Operand, usedSpOffsets, ref spAddressTaken);
                return;
            case ExprField field:
                MarkStackUsage(field.Target, usedSpOffsets, ref spAddressTaken);
                return;
            case ExprIndex index:
                MarkStackUsage(index.Target, usedSpOffsets, ref spAddressTaken);
                MarkStackUsage(index.Index, usedSpOffsets, ref spAddressTaken);
                return;
            case ExprNew newExpr:
                foreach (var arg in newExpr.Args)
                    MarkStackUsage(arg, usedSpOffsets, ref spAddressTaken);
                MarkStackUsage(newExpr.Size, usedSpOffsets, ref spAddressTaken);
                return;
            case ExprStructInit structInit:
                foreach (var field in structInit.Fields)
                    MarkStackUsage(field.Value, usedSpOffsets, ref spAddressTaken);
                return;
            case ExprTernary ternary:
                MarkStackUsage(ternary.Condition, usedSpOffsets, ref spAddressTaken);
                MarkStackUsage(ternary.TrueValue, usedSpOffsets, ref spAddressTaken);
                MarkStackUsage(ternary.FalseValue, usedSpOffsets, ref spAddressTaken);
                return;
        }
    }

    private readonly record struct StackUsage(HashSet<long> UsedSpOffsets, bool SpAddressTaken);
}
