using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.IR;

/// <summary>
/// Removes IL2CPP runtime-only control-flow edges before SSA so boilerplate paths
/// cannot contribute phi sources to semantic code.
/// </summary>
public static class SemanticCfgPruner
{
    public static int PruneClassInitFallbackEdges(IrControlFlowGraph cfg)
    {
        int removed = 0;

        foreach (var block in cfg.Blocks)
        {
            if (!IsClassInitFallbackBlock(block))
                continue;

            var successor = block.Successors.Count == 1 ? block.Successors[0].Target : null;
            if (successor == null)
                continue;

            var incoming = block.Predecessors.ToArray();
            bool anyRemoved = false;

            foreach (var edge in incoming)
            {
                if (!IsClassInitFallbackPredecessor(edge.Source, block))
                    continue;

                edge.Source.Successors.Remove(edge);
                block.Predecessors.Remove(edge);
                removed++;
                anyRemoved = true;
            }

            if (anyRemoved && block.Predecessors.Count == 0)
            {
                foreach (var outEdge in block.Successors.ToArray())
                {
                    outEdge.Target.Predecessors.Remove(outEdge);
                    block.Successors.Remove(outEdge);
                }
            }
        }

        if (removed > 0 && ConsoleReporter.IsTracing)
            ConsoleReporter.Trace($"SemanticCfgPruner: removed {removed} class-init fallback edge(s) from {cfg.Method.MethodName}");

        return removed;
    }

    private static bool IsClassInitFallbackBlock(IrBasicBlock block)
    {
        if (block.Instructions.Count != 1 || block.Successors.Count != 1)
            return false;

        var inst = block.Instructions[0];
        return inst.Opcode is IrOpcode.Call or IrOpcode.RuntimeHelper
            && IsRuntimeHelperAnnotation(inst.Annotation);
    }

    private static bool IsClassInitFallbackPredecessor(IrBasicBlock pred, IrBasicBlock fallback)
    {
        if (pred.Successors.Count == 1)
            return pred.TerminatorKind == IrTerminatorKind.UnconditionalBranch
                && pred.Successors[0].Target == fallback
                && pred.Instructions.Count == 1
                && pred.Terminator.Opcode == IrOpcode.Branch;

        if (pred.TerminatorKind != IrTerminatorKind.ConditionalBranch || pred.Successors.Count != 2)
            return false;

        return pred.Successors.Any(edge => edge.Target == fallback)
            && ContainsClassInitFlagLoad(pred);
    }

    private static bool ContainsClassInitFlagLoad(IrBasicBlock block)
        => block.Instructions.Any(inst =>
            inst.SemanticTag == IrSemanticTag.ClassInit ||
            (inst.Opcode == IrOpcode.Load &&
             inst.Sources.Length > 0 &&
             inst.Sources[0].Kind == IrOperandKind.Memory &&
             inst.Sources[0].Offset == Rosetta.Common.Constants.ClassInitFlagOffset));

    private static bool IsRuntimeHelperAnnotation(string? annotation)
        => annotation != null &&
           (annotation.Contains("il2cpp_runtime_helper", StringComparison.Ordinal) ||
            annotation.StartsWith("new ", StringComparison.Ordinal) ||
            annotation.Contains("runtime_helper", StringComparison.Ordinal));
}
