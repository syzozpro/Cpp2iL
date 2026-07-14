using Rosetta.Pipeline;

namespace Rosetta.Analysis.IR.SSA;

/// <summary>
/// Computes the dominance frontier for each block in the CFG.
///
/// The dominance frontier of block B is the set of blocks where B's dominance
/// "just ends" — i.e., blocks that have a predecessor dominated by B but are
/// NOT themselves strictly dominated by B.
///
/// This is where phi nodes must be placed: if variable V is defined in block B,
/// then a phi for V is needed at every block in DF(B).
///
/// Algorithm: "Efficiently Computing Static Single Assignment Form and the
/// Control Dependence Graph" (Cytron et al., 1991)
/// </summary>
public sealed class DominanceFrontier
{
    /// <summary>DF[blockId] = set of block IDs in the dominance frontier.</summary>
    public HashSet<int>[] Frontiers { get; }

    private DominanceFrontier(int blockCount)
    {
        Frontiers = new HashSet<int>[blockCount];
        for (int i = 0; i < blockCount; i++)
            Frontiers[i] = [];
    }

    /// <summary>
    /// Compute dominance frontiers from a CFG and its dominator tree.
    /// </summary>
    public static DominanceFrontier Build(IrControlFlowGraph cfg, DominatorTree domTree)
    {
        var df = new DominanceFrontier(cfg.Blocks.Count);

        foreach (var block in cfg.Blocks)
        {
            // Only blocks with 2+ predecessors (join points) contribute to DF
            if (block.Predecessors.Count < 2)
                continue;

            foreach (var edge in block.Predecessors)
            {
                int runner = edge.Source.Id;

                // Walk up the dominator tree from the predecessor to the
                // immediate dominator of the current block, adding the
                // current block to each runner's frontier.
                while (runner != -1 && runner != domTree.IDom[block.Id])
                {
                    df.Frontiers[runner].Add(block.Id);
                    if (runner == domTree.IDom[runner])
                        break; // reached root
                    runner = domTree.IDom[runner];
                }
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  DominanceFrontier: {df.Frontiers.Count(f => f.Count > 0)} blocks have non-empty frontiers");
        return df;
    }

    /// <summary>
    /// Compute the iterated dominance frontier for a set of blocks.
    /// This is the transitive closure of DF — all blocks where phis might be needed
    /// for a variable defined in any of the given blocks.
    /// </summary>
    public HashSet<int> IteratedFrontier(IEnumerable<int> defBlocks)
    {
        var result = new HashSet<int>();
        var worklist = new Queue<int>(defBlocks);

        while (worklist.Count > 0)
        {
            int blockId = worklist.Dequeue();
            foreach (int dfBlock in Frontiers[blockId])
            {
                if (result.Add(dfBlock))
                    worklist.Enqueue(dfBlock);
            }
        }

        return result;
    }
}
