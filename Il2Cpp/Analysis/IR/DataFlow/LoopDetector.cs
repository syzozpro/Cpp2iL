using System.Collections.Generic;

namespace Rosetta.Analysis.IR.DataFlow;

/// <summary>
/// Information about a natural loop in the control flow graph.
/// </summary>
public sealed class LoopInfo
{
    public int HeaderBlockId { get; set; }
    public HashSet<int> BodyBlockIds { get; set; } = new();
    public int ExitBlockId { get; set; } = -1;
}

/// <summary>
/// Detects natural loops in an IrControlFlowGraph.
/// </summary>
public static class LoopDetector
{
    /// <summary>
    /// Detect all natural loops in the CFG by finding back-edges.
    /// A back-edge is an edge A → B where B has a lower or equal RPO index than A.
    /// For each back-edge, computes the natural loop body (all blocks that can
    /// reach the latch from the header without leaving the loop).
    /// </summary>
    public static Dictionary<int, LoopInfo> DetectNaturalLoops(IrControlFlowGraph cfg)
    {
        var loops = new Dictionary<int, LoopInfo>();
        var rpoOrder = cfg.ReachableReversePostOrder();
        
        var rpoIndex = new Dictionary<int, int>(rpoOrder.Count);
        for (int i = 0; i < rpoOrder.Count; i++)
            rpoIndex[rpoOrder[i].Id] = i;

        foreach (var block in cfg.Blocks)
        {
            if (!rpoIndex.ContainsKey(block.Id)) continue; // skip unreachable

            foreach (var edge in block.Successors)
            {
                if (!rpoIndex.ContainsKey(edge.Target.Id)) continue;

                int sourceRpo = rpoIndex[block.Id];
                int targetRpo = rpoIndex[edge.Target.Id];

                // Back-edge: source has higher or equal RPO index than target
                if (sourceRpo >= targetRpo)
                {
                    int headerId = edge.Target.Id;

                    if (!loops.TryGetValue(headerId, out var loopInfo))
                    {
                        loopInfo = new LoopInfo { HeaderBlockId = headerId };
                        loops[headerId] = loopInfo;
                    }

                    // Compute natural loop body: header + all blocks that can reach latch without leaving
                    ComputeLoopBody(headerId, block.Id, cfg, loopInfo.BodyBlockIds);
                }
            }
        }

        // Compute exit blocks: first successor of any loop block that's outside the loop
        foreach (var (headerId, loopInfo) in loops)
        {
            // Check body blocks in RPO order for deterministic exit selection
            foreach (var bodyBlockId in rpoOrder)
            {
                if (!loopInfo.BodyBlockIds.Contains(bodyBlockId.Id)) continue;
                var bodyBlock = bodyBlockId;

                foreach (var edge in bodyBlock.Successors)
                {
                    if (!loopInfo.BodyBlockIds.Contains(edge.Target.Id))
                    {
                        loopInfo.ExitBlockId = edge.Target.Id;
                        break;
                    }
                }
                if (loopInfo.ExitBlockId >= 0) break;
            }
        }

        return loops;
    }

    /// <summary>
    /// Compute the natural loop body for a back-edge latch → header.
    /// Walks backwards from latch, adding all blocks that can reach latch
    /// from within the loop (stopping at the header boundary).
    /// </summary>
    private static void ComputeLoopBody(int headerId, int latchId, IrControlFlowGraph cfg, HashSet<int> body)
    {
        body.Add(headerId);
        if (headerId == latchId) return; // self-loop

        var worklist = new Stack<int>();
        if (body.Add(latchId))
            worklist.Push(latchId);

        while (worklist.Count > 0)
        {
            int current = worklist.Pop();
            var block = cfg.FindBlock(current);
            if (block == null) continue;

            foreach (var pred in block.Predecessors)
            {
                if (body.Add(pred.Source.Id))
                    worklist.Push(pred.Source.Id);
            }
        }
    }
}
