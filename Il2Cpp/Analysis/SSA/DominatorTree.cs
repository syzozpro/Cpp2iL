using Rosetta.Pipeline;

namespace Rosetta.Analysis.IR.SSA;

/// <summary>
/// Computes the dominator tree for an IR control flow graph.
///
/// Algorithm: Cooper, Harvey, Kennedy — "A Simple, Fast Dominance Algorithm" (2001).
/// This iterative algorithm is simpler than Lengauer-Tarjan and fast enough for
/// methods up to thousands of blocks.
///
/// Key properties:
///   - IDom[b] = the immediate dominator of block b
///   - Block A dominates block B if every path from the entry to B passes through A
///   - The dominator tree is used to compute dominance frontiers for phi placement
///
/// The entry block (BB0) dominates all reachable blocks and has no dominator itself.
/// </summary>
public sealed class DominatorTree
{
    /// <summary>Immediate dominator for each block. IDom[blockId] = dominator's blockId.</summary>
    public int[] IDom { get; }

    /// <summary>Children in the dominator tree. DomChildren[blockId] = list of dominated block IDs.</summary>
    public List<int>[] DomChildren { get; }

    /// <summary>Reverse post-order numbering. RpoNumber[blockId] = RPO position (lower = earlier).</summary>
    public int[] RpoNumber { get; }

    /// <summary>Number of blocks in the graph.</summary>
    public int BlockCount { get; }

    private DominatorTree(int blockCount)
    {
        BlockCount = blockCount;
        IDom = new int[blockCount];
        RpoNumber = new int[blockCount];
        DomChildren = new List<int>[blockCount];

        for (int i = 0; i < blockCount; i++)
        {
            IDom[i] = -1; // undefined
            DomChildren[i] = [];
        }
    }

    /// <summary>
    /// Build the dominator tree from a control flow graph.
    /// </summary>
    public static DominatorTree Build(IrControlFlowGraph cfg)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"  DominatorTree.Build(): {cfg.Blocks.Count} blocks");
        var tree = new DominatorTree(cfg.Blocks.Count);
        if (cfg.Blocks.Count == 0)
            return tree;

        // Step 1: Compute reverse post-order
        var rpo = cfg.ReversePostOrder();
        for (int i = 0; i < rpo.Count; i++)
            tree.RpoNumber[rpo[i].Id] = i;

        // Mark unreachable blocks
        var reachable = new HashSet<int>(rpo.Select(b => b.Id));

        // Step 2: Initialize entry block
        int entryId = cfg.EntryBlock.Id;
        tree.IDom[entryId] = entryId; // Entry dominates itself

        // Step 3: Iterate until convergence
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in rpo)
            {
                if (block.Id == entryId) continue;

                // Find first processed predecessor
                int newIdom = -1;
                foreach (var edge in block.Predecessors)
                {
                    int predId = edge.Source.Id;
                    if (!reachable.Contains(predId)) continue;
                    if (tree.IDom[predId] != -1) // already processed
                    {
                        newIdom = predId;
                        break;
                    }
                }

                if (newIdom == -1) continue; // no processed predecessors yet

                // Intersect with other processed predecessors
                foreach (var edge in block.Predecessors)
                {
                    int predId = edge.Source.Id;
                    if (predId == newIdom) continue;
                    if (!reachable.Contains(predId)) continue;
                    if (tree.IDom[predId] != -1) // already processed
                        newIdom = Intersect(tree, predId, newIdom);
                }

                if (tree.IDom[block.Id] != newIdom)
                {
                    tree.IDom[block.Id] = newIdom;
                    changed = true;
                }
            }
        }

        // Step 4: Blocks unreachable from the entry (exception handlers, async state
        // machine dispatch targets) have no real predecessor in the RPO list and thus
        // have IDom == -1. Assign them to be dominated by the entry block so they're
        // included in the dominator tree and processed by SSA construction.
        //
        // CRITICAL: We must iterate cfg.Blocks (ALL physical blocks), not the `rpo`
        // list. The RPO list only contains blocks reachable from the entry via DFS,
        // so disjoint catch handlers are inherently absent from it. Iterating `rpo`
        // here made Step 4 dead code — IDom[b] == -1 was never true in that loop.
        foreach (var block in cfg.Blocks)
        {
            if (block.Id != entryId && tree.IDom[block.Id] == -1)
            {
                tree.IDom[block.Id] = entryId;
                // Assign a pseudo-RPO number so Intersect() works for these blocks
                // Use a number beyond the reachable set (they're "late" in traversal)
                if (!reachable.Contains(block.Id))
                    tree.RpoNumber[block.Id] = rpo.Count + block.Id;
            }
        }

        // Step 5: Build dominator tree children lists
        for (int i = 0; i < tree.BlockCount; i++)
        {
            if (tree.IDom[i] != -1 && tree.IDom[i] != i)
                tree.DomChildren[tree.IDom[i]].Add(i);
        }

        if (ConsoleReporter.IsTracing)
        {
            int edgeCount = 0;
            for (int i = 0; i < tree.BlockCount; i++) edgeCount += tree.DomChildren[i].Count;
            ConsoleReporter.Debug($"  DominatorTree done: {edgeCount} parent-child edges");
        }
        return tree;
    }

    /// <summary>
    /// Intersect two blocks in the dominator tree to find their common dominator.
    /// Walks up the tree from both blocks until they meet.
    /// </summary>
    private static int Intersect(DominatorTree tree, int b1, int b2)
    {
        int finger1 = b1, finger2 = b2;
        while (finger1 != finger2)
        {
            while (tree.RpoNumber[finger1] > tree.RpoNumber[finger2])
            {
                finger1 = tree.IDom[finger1];
                if (finger1 < 0) return finger2; // safety
            }
            while (tree.RpoNumber[finger2] > tree.RpoNumber[finger1])
            {
                finger2 = tree.IDom[finger2];
                if (finger2 < 0) return finger1; // safety
            }
        }
        return finger1;
    }

    /// <summary>Does block A dominate block B?</summary>
    public bool Dominates(int a, int b)
    {
        if (a == b) return true;
        int runner = b;
        while (runner != -1 && runner != IDom[runner])
        {
            if (runner == a) return true;
            runner = IDom[runner];
        }
        // The loop exits when runner reaches the entry block (IDom[entry] = entry).
        // We must check one final time if runner == a — otherwise the entry block
        // is never recognized as dominating any other block.
        return runner == a;
    }

    /// <summary>Pre-order traversal of the dominator tree (needed for SSA renaming).</summary>
    public List<int> PreOrder()
    {
        var result = new List<int>();
        if (BlockCount > 0)
            DfsPreOrder(IDom[0] == 0 ? 0 : FindRoot(), result);
        return result;
    }

    private int FindRoot()
    {
        for (int i = 0; i < BlockCount; i++)
            if (IDom[i] == i) return i;
        return 0;
    }

    private void DfsPreOrder(int blockId, List<int> result)
    {
        result.Add(blockId);
        foreach (int child in DomChildren[blockId])
            DfsPreOrder(child, result);
    }
}
