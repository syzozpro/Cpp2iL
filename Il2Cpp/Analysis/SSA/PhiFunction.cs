namespace Rosetta.Analysis.IR.SSA;

/// <summary>
/// A phi function placed at the entry of a basic block where control flow merges.
///
/// Semantics: dst = φ(src₁ from BB_a, src₂ from BB_b, ...)
/// At runtime, the value of dst is the src that came from the actually-taken predecessor.
///
/// Phi nodes are not real instructions — they're bookkeeping for SSA construction.
/// They tell us "this variable has different values depending on which path we took to get here."
///
/// Example:
///   BB2 has predecessors BB0 and BB1.
///   BB0 defines x8₁, BB1 defines x8₂.
///   At BB2 entry: x8₃ = φ(x8₁ from BB0, x8₂ from BB1)
/// </summary>
public sealed class PhiFunction
{
    /// <summary>The block where this phi is placed.</summary>
    public int BlockId { get; }

    /// <summary>The variable being defined by this phi.</summary>
    public SsaVariable Destination { get; set; }

    /// <summary>
    /// Sources: one per predecessor block.
    /// Each entry is (source variable, predecessor block ID).
    /// Filled during the renaming phase.
    /// </summary>
    public List<PhiSource> Sources { get; } = [];

    /// <summary>The logical variable (VarId without version) this phi is for.</summary>
    public int VarId { get; }

    public PhiFunction(int blockId, int varId)
    {
        BlockId = blockId;
        VarId = varId;
        Destination = SsaVariable.Undefined;
    }

    public override string ToString()
    {
        string srcs = Sources.Count > 0
            ? string.Join(", ", Sources.Select(s => $"{s.Variable} from BB{s.PredecessorBlockId}"))
            : "...";
        return $"{Destination} = φ({srcs})";
    }
}

/// <summary>
/// A single source in a phi function: the SSA variable coming from a specific predecessor block.
/// </summary>
public readonly struct PhiSource
{
    public readonly SsaVariable Variable;
    public readonly int PredecessorBlockId;

    public PhiSource(SsaVariable variable, int predecessorBlockId)
    {
        Variable = variable;
        PredecessorBlockId = predecessorBlockId;
    }

    public override string ToString() => $"{Variable} from BB{PredecessorBlockId}";
}
