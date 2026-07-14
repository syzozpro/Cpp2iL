namespace Rosetta.Analysis.IR;

/// <summary>
/// Classifies how control flows between two IR basic blocks.
/// Designed for SSA construction: dominator trees need edge-kind info
/// to correctly place phi nodes at conditional merge points.
/// </summary>
public enum IrEdgeKind : byte
{
    /// <summary>No branch — execution falls through to the next sequential block.</summary>
    Fallthrough,

    /// <summary>Unconditional branch (IR: Branch → goto target).</summary>
    Unconditional,

    /// <summary>Conditional branch taken — the "true" path (IR: ConditionalBranch, CBZ, TBNZ, etc.).</summary>
    ConditionalTrue,

    /// <summary>Conditional branch not taken — the "false" / fallthrough path.</summary>
    ConditionalFalse,
}
