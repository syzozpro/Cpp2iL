using Rosetta.Lifter.IR;

namespace Rosetta.Analysis.IR;

/// <summary>
/// The complete IR-level control flow graph for a single method.
///
/// Contains the ordered list of basic blocks and provides traversal helpers
/// for downstream passes (dominator tree, SSA construction, loop detection).
///
/// Built by <see cref="IrCfgBuilder"/> from an <see cref="IrMethod"/>.
/// </summary>
public sealed class IrControlFlowGraph
{
    /// <summary>The method this CFG was built from.</summary>
    public IrMethod Method { get; }

    /// <summary>All basic blocks, ordered by start address. BB0 = entry.</summary>
    public List<IrBasicBlock> Blocks { get; }

    /// <summary>The entry block (always Blocks[0]).</summary>
    public IrBasicBlock EntryBlock => Blocks[0];

    /// <summary>All blocks that end with a Return terminator.</summary>
    public IEnumerable<IrBasicBlock> ExitBlocks
        => Blocks.Where(b => b.TerminatorKind == IrTerminatorKind.Return
                          || b.TerminatorKind == IrTerminatorKind.TailCall);

    /// <summary>Total number of edges in the graph.</summary>
    public int EdgeCount => Blocks.Sum(b => b.Successors.Count);

    public IrControlFlowGraph(IrMethod method, List<IrBasicBlock> blocks)
    {
        Method = method;
        Blocks = blocks;
    }

    /// <summary>
    /// Reverse Post-Order traversal — the iteration order needed for
    /// dominance frontier computation (Lengauer-Tarjan algorithm).
    ///
    /// Also includes blocks unreachable from the entry (exception landing pads,
    /// async state machine dispatch targets) so the dominator tree covers all blocks.
    /// </summary>
    public List<IrBasicBlock> ReversePostOrder()
    {
        var visited = new HashSet<int>();
        var postOrder = new List<IrBasicBlock>();
        DfsPostOrder(EntryBlock, visited, postOrder);

        // Include blocks not reachable from entry (exception handlers, etc.)
        // These are appended after the main RPO so they get higher RPO numbers.
        if (visited.Count < Blocks.Count)
        {
            foreach (var block in Blocks)
            {
                if (!visited.Contains(block.Id))
                    DfsPostOrder(block, visited, postOrder);
            }
        }

        postOrder.Reverse();
        return postOrder;
    }

    /// <summary>
    /// Computes RPO block IDs, but ONLY for blocks reachable from the entry block.
    /// Used for AST building and expression propagation where dead blocks can be ignored.
    /// </summary>
    public List<IrBasicBlock> ReachableReversePostOrder()
    {
        var visited = new HashSet<int>();
        var postOrder = new List<IrBasicBlock>();
        DfsPostOrder(EntryBlock, visited, postOrder);
        postOrder.Reverse();
        return postOrder;
    }


    /// <summary>
    /// Depth-first search traversal from the entry block.
    /// </summary>
    public List<IrBasicBlock> DepthFirstOrder()
    {
        var visited = new HashSet<int>();
        var result = new List<IrBasicBlock>();
        DfsPre(EntryBlock, visited, result);
        return result;
    }

    /// <summary>
    /// Find a block by its ID.
    /// </summary>
    public IrBasicBlock? FindBlock(int id)
        => id >= 0 && id < Blocks.Count ? Blocks[id] : null;

    /// <summary>
    /// Find the block that contains the given address.
    /// Used by the edge-connection phase to resolve branch targets.
    /// </summary>
    public IrBasicBlock? FindBlockByAddress(ulong address)
    {
        // Binary search since blocks are sorted by start address
        int lo = 0, hi = Blocks.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var block = Blocks[mid];
            if (address < block.StartAddress)
                hi = mid - 1;
            else if (address > block.EndAddress)
                lo = mid + 1;
            else
                return block;
        }
        return null;
    }

    // ── Private DFS helpers ────────────────────────────────────────────────

    private void DfsPostOrder(IrBasicBlock block, HashSet<int> visited, List<IrBasicBlock> result)
    {
        if (!visited.Add(block.Id)) return;
        foreach (var edge in block.Successors)
            DfsPostOrder(edge.Target, visited, result);
        result.Add(block);
    }

    private void DfsPre(IrBasicBlock block, HashSet<int> visited, List<IrBasicBlock> result)
    {
        if (!visited.Add(block.Id)) return;
        result.Add(block);
        foreach (var edge in block.Successors)
            DfsPre(edge.Target, visited, result);
    }

    // ── Display ────────────────────────────────────────────────────────────

    public override string ToString()
        => $"CFG({Method.MethodName}): {Blocks.Count} blocks, {EdgeCount} edges";
}
