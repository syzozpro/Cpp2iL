using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Analysis.IR;

/// <summary>
/// A basic block in the IR-level control flow graph.
///
/// Invariants:
///   1. No branches except at the end (the terminator).
///   2. No branch targets except at the beginning (the leader).
///   3. Execution enters only at the top, exits only at the bottom.
///
/// SSA-ready: Predecessors/Successors are populated by <see cref="IrCfgBuilder"/>
/// and consumed by dominator tree construction and phi-node insertion.
/// </summary>
public sealed class IrBasicBlock
{
    /// <summary>Unique block index (0 = entry block, sequential thereafter).</summary>
    public int Id { get; internal set; }

    /// <summary>IR instructions in this block, in program order.</summary>
    public List<IrInstruction> Instructions { get; }

    /// <summary>Incoming edges (blocks that jump/fall-through to this block).</summary>
    public List<IrBlockEdge> Predecessors { get; } = [];

    /// <summary>Outgoing edges (blocks this block jumps/falls-through to).</summary>
    public List<IrBlockEdge> Successors { get; } = [];

    /// <summary>Address of the first instruction (leader address).</summary>
    public ulong StartAddress => Instructions.Count > 0 ? Instructions[0].Address : 0;

    /// <summary>Address of the last instruction (terminator address).</summary>
    public ulong EndAddress => Instructions.Count > 0 ? Instructions[^1].Address : 0;

    /// <summary>The terminator instruction (last instruction that determines outgoing edges).</summary>
    public IrInstruction Terminator => Instructions[^1];

    /// <summary>Number of instructions in this block.</summary>
    public int Count => Instructions.Count;

    /// <summary>What kind of terminator ends this block.</summary>
    public IrTerminatorKind TerminatorKind { get; internal set; }

    public IrBasicBlock(int id, List<IrInstruction> instructions)
    {
        Id = id;
        Instructions = instructions;
        TerminatorKind = ClassifyTerminator(instructions);
    }

    /// <summary>Add an outgoing edge to a successor block.</summary>
    public void AddSuccessor(IrBasicBlock target, IrEdgeKind kind)
    {
        var edge = new IrBlockEdge(this, target, kind);
        Successors.Add(edge);
        target.Predecessors.Add(edge);
    }

    // ── Terminator Classification ──────────────────────────────────────────

    private static IrTerminatorKind ClassifyTerminator(List<IrInstruction> instructions)
    {
        if (instructions.Count == 0)
            return IrTerminatorKind.Fallthrough;

        var last = instructions[^1];

        // A Call to a noreturn function (e.g., __cxa_throw) is an unreachable terminator.
        // Check this before the default fallthrough to prevent phantom edges.
        if (last.IsNoReturn)
            return IrTerminatorKind.Unreachable;

        return last.Opcode switch
        {
            IrOpcode.Branch     => IrTerminatorKind.UnconditionalBranch,
            IrOpcode.TailCall   => IrTerminatorKind.TailCall,
            IrOpcode.Return     => IrTerminatorKind.Return,
            IrOpcode.ConditionalBranch => IrTerminatorKind.ConditionalBranch,
            IrOpcode.IndirectBranch    => IrTerminatorKind.IndirectBranch,
            _ => IrTerminatorKind.Fallthrough,
        };
    }

    public override string ToString()
        => $"BB{Id} [0x{StartAddress:X}..0x{EndAddress:X}] ({Count} ops, {TerminatorKind})";
}

/// <summary>
/// A directed edge between two basic blocks with its kind.
/// Stores references to both endpoints for bidirectional traversal.
/// </summary>
public sealed class IrBlockEdge
{
    public IrBasicBlock Source { get; }
    public IrBasicBlock Target { get; }
    public IrEdgeKind Kind { get; }

    public IrBlockEdge(IrBasicBlock source, IrBasicBlock target, IrEdgeKind kind)
    {
        Source = source;
        Target = target;
        Kind = kind;
    }

    public override string ToString() => $"BB{Source.Id} →[{Kind}]→ BB{Target.Id}";
}

/// <summary>
/// How a basic block's execution terminates.
/// </summary>
public enum IrTerminatorKind : byte
{
    /// <summary>Block falls through to the next sequential block.</summary>
    Fallthrough,
    /// <summary>Unconditional branch (goto target).</summary>
    UnconditionalBranch,
    /// <summary>Conditional branch (if-goto with fallthrough).</summary>
    ConditionalBranch,
    /// <summary>Tail call — branch to another method (no return).</summary>
    TailCall,
    /// <summary>Return — method exit point.</summary>
    Return,
    /// <summary>Indirect branch — target computed at runtime.</summary>
    IndirectBranch,
    /// <summary>Unreachable — block ends with a noreturn call (e.g., __cxa_throw). No successors.</summary>
    Unreachable,
}
