using System.Collections.Generic;
using System.Linq;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

public sealed partial class ExprPropagator
{
    private void ProcessReachableBlocks(IReadOnlyList<int> rpoOrder)
    {
        foreach (int blockId in rpoOrder)
        {
            var block = _cfg.FindBlock(blockId);
            if (block == null)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  block {blockId}: not found, skipping");
                continue;
            }

            var stmts = new List<ExprStatement>();
            BlockStatements[blockId] = stmts;

            if (ConsoleReporter.IsTracing)
                ConsoleReporter.Trace($"  block {blockId}: {block.Instructions.Count} instructions, {_ssa.GetPhis(blockId).Count} phis");

            foreach (var phi in _ssa.GetPhis(blockId))
            {
                var phiExpr = new ExprVar(phi.Destination.Name, phi.Destination.VarId, phi.Destination.Version);
                ExprMap[phi.Destination] = phiExpr;
                if (ConsoleReporter.IsTracing)
                    ConsoleReporter.Debug($"    phi: {phi.Destination.Name} v{phi.Destination.Version} -> ExprVar({phiExpr.Emit()})");
            }

            for (int i = 0; i < block.Instructions.Count; i++)
                ProcessInstruction(block.Instructions[i], blockId, i, stmts);

            if (ConsoleReporter.IsTracing)
                ConsoleReporter.Trace($"  block {blockId}: produced {stmts.Count} statements");
        }
    }

    private void DeconstructPhis(IReadOnlyList<int> rpoOrder)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace("  phi deconstruction pass...");

        var orderIndex = rpoOrder
            .Select((blockId, index) => (blockId, index))
            .ToDictionary(x => x.blockId, x => x.index);

        var phiDests = new HashSet<SsaVariable>();
        foreach (int blockId in rpoOrder)
        {
            foreach (var phi in _ssa.GetPhis(blockId))
            {
                phiDests.Add(phi.Destination);
            }
        }

        foreach (int blockId in rpoOrder)
        {
            foreach (var phi in _ssa.GetPhis(blockId))
            {
                foreach (var src in phi.Sources)
                    AddPhiAssignment(blockId, src.PredecessorBlockId, phi, src.Variable, orderIndex, phiDests);
            }
        }
    }

    private void AddPhiAssignment(int blockId, int predBlockId, PhiFunction phi,
        SsaVariable source, IReadOnlyDictionary<int, int> orderIndex, HashSet<SsaVariable> phiDests)
    {
        if ((phi.Destination.IsStackSlot && _ctx.CalleeSavedSpillSlots.Contains(phi.Destination.VarId - 200)) ||
            (source.IsStackSlot && _ctx.CalleeSavedSpillSlots.Contains(source.VarId - 200)))
        {
            if (ConsoleReporter.IsTracing)
                ConsoleReporter.Debug($"    phi-skip callee-saved spill: dest={phi.Destination.Name}, src={source.Name}");
            return;
        }

        if (!BlockStatements.ContainsKey(predBlockId))
            BlockStatements[predBlockId] = new List<ExprStatement>();

        var target = MakeVarExpr(phi.Destination);
        ExprNode value;
        int srcUseCount = _defUse.UseCount(source);
        if (srcUseCount <= 1 && !phiDests.Contains(source) && ExprMap.TryGetValue(source, out var inlinedExpr))
        {
            value = inlinedExpr;
            Inlined.Add(source);
            if (ConsoleReporter.IsTracing)
                ConsoleReporter.Debug($"    phi-inline: {source.Name} v{source.Version} (useCount={srcUseCount}) -> {value.Emit()}");
        }
        else
        {
            value = Resolve(source);
        }

        if (value is ExprVar srcVar && target is ExprVar dstVar && srcVar.Name == dstVar.Name)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"    phi-skip trivial: {srcVar.Name} == {dstVar.Name}");
            return;
        }

        int blockOrder = GetOrderIndex(orderIndex, blockId);
        int forwardEdges = phi.Sources
            .Count(src => GetOrderIndex(orderIndex, src.PredecessorBlockId) < blockOrder);

        bool isLoopPhi = forwardEdges == 1;
        bool isPreHeader = GetOrderIndex(orderIndex, predBlockId) < blockOrder;

        BlockStatements[predBlockId].Add(new ExprStatement
        {
            Expr = new ExprAssign(target, value),
            IsDeclaration = isLoopPhi && isPreHeader,
            SsaVar = phi.Destination
        });

        if (ConsoleReporter.IsTracing)
            ConsoleReporter.Debug($"    phi-assign: pred={predBlockId} {target.Emit()} = {value.Emit()} (isDecl={isLoopPhi && isPreHeader})");
    }

    private static int GetOrderIndex(IReadOnlyDictionary<int, int> orderIndex, int blockId)
        => orderIndex.TryGetValue(blockId, out int index) ? index : -1;
}