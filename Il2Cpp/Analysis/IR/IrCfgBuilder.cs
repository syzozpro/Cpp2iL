using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.IR;

/// <summary>
/// Builds an IR-level Control Flow Graph from an <see cref="IrMethod"/>.
///
/// Algorithm (classic textbook "leader identification"):
///   Phase 1 — Find leaders:
///     a) The first instruction is always a leader.
///     b) The target of any Branch/ConditionalBranch is a leader.
///     c) The instruction immediately after a Branch/ConditionalBranch/Return is a leader.
///
///   Phase 2 — Split into blocks:
///     Each leader starts a new basic block. Instructions between two consecutive
///     leaders belong to the first leader's block.
///
///   Phase 3 — Connect edges:
///     Walk each block's terminator and create Successor/Predecessor links.
///
/// Designed for SSA: the resulting graph has bidirectional edges and supports
/// Reverse Post-Order traversal for dominator tree computation.
/// </summary>
public sealed class IrCfgBuilder
{
    /// <summary>
    /// Build a control flow graph from the given IR method.
    /// Returns null if the method has no instructions.
    /// </summary>
    public IrControlFlowGraph? Build(IrMethod method)
    {
        var instructions = method.Instructions;
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"IrCfgBuilder.Build() START: {method.MethodName} ({instructions.Count} instructions)");
        if (instructions.Count == 0)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  CFG: no instructions, returning null");
            return null;
        }

        // Sort instructions by address to ensure correct block boundaries.
        // The lifter usually emits in address order, so check first to avoid O(n log n).
        bool needsSort = false;
        for (int i = 1; i < instructions.Count; i++)
        {
            if (instructions[i].Address < instructions[i - 1].Address)
            {
                needsSort = true;
                break;
            }
        }
        if (needsSort)
            instructions.Sort((a, b) => a.Address.CompareTo(b.Address));

        // ── Phase 1: Find all leader addresses ────────────────────────────
        var leaders = FindLeaders(instructions);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  CFG Phase 1: {leaders.Count} leaders found");

        // ── Phase 2: Split instructions into basic blocks ─────────────────
        var blocks = SplitIntoBlocks(instructions, leaders);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  CFG Phase 2: {blocks.Count} blocks");
        if (blocks.Count == 0)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  CFG: no blocks, returning null");
            return null;
        }

        // ── Phase 3: Connect edges ────────────────────────────────────────
        var addressToBlock = BuildAddressMap(blocks);
        ConnectEdges(blocks, addressToBlock);

        if (ConsoleReporter.IsTracing)
        {
            int totalEdges = 0;
            for (int i = 0; i < blocks.Count; i++) totalEdges += blocks[i].Successors.Count;
            ConsoleReporter.Trace($"IrCfgBuilder.Build() END: {blocks.Count} blocks, {totalEdges} edges");
        }

        return new IrControlFlowGraph(method, blocks);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 1: Leader Identification
    // ═══════════════════════════════════════════════════════════════════════

    private HashSet<ulong> FindLeaders(List<IrInstruction> instructions)
    {
        var leaders = new HashSet<ulong>();

        // Rule (a): First instruction is always a leader
        leaders.Add(instructions[0].Address);

        for (int i = 0; i < instructions.Count; i++)
        {
            var inst = instructions[i];

            switch (inst.Opcode)
            {
                case IrOpcode.Branch:
                {
                    // Rule (b): Branch target is a leader
                    ulong target = ExtractBranchTarget(inst);
                    if (target != 0)
                        leaders.Add(target);

                    // Rule (c): Instruction after branch is a leader
                    if (i + 1 < instructions.Count)
                        leaders.Add(instructions[i + 1].Address);
                    break;
                }

                case IrOpcode.TailCall:
                {
                    // TailCall is a method exit — its target is an external function.
                    // Do NOT add the target as a leader (it's not an intra-method block).
                    // Only the instruction after the tail call starts a new block.
                    if (i + 1 < instructions.Count)
                        leaders.Add(instructions[i + 1].Address);
                    break;
                }

                case IrOpcode.ConditionalBranch:
                {
                    // Rule (b): Branch target is a leader (the "taken" path)
                    ulong target = ExtractCondBranchTarget(inst);
                    if (target != 0)
                        leaders.Add(target);

                    // Rule (c): Next instruction is a leader (the "not-taken" / fallthrough path)
                    if (i + 1 < instructions.Count)
                        leaders.Add(instructions[i + 1].Address);
                    break;
                }

                case IrOpcode.Return:
                {
                    // Rule (c): Instruction after return is a leader (next method section or dead code)
                    if (i + 1 < instructions.Count)
                        leaders.Add(instructions[i + 1].Address);
                    break;
                }

                // Noreturn calls (__cxa_throw, abort, etc.) terminate execution like Return.
                // The instruction after a noreturn call is a leader for any code that
                // branches to that address from elsewhere.
                case IrOpcode.Call:
                case IrOpcode.IndirectCall:
                {
                    if (inst.IsNoReturn && i + 1 < instructions.Count)
                        leaders.Add(instructions[i + 1].Address);
                    break;
                }
            }
        }

        return leaders;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 2: Block Splitting
    // ═══════════════════════════════════════════════════════════════════════

    private List<IrBasicBlock> SplitIntoBlocks(List<IrInstruction> instructions, HashSet<ulong> leaders)
    {
        var blocks = new List<IrBasicBlock>();
        var currentBlockInsts = new List<IrInstruction>();
        int blockId = 0;
        ulong lastAddress = ulong.MaxValue;

        foreach (var inst in instructions)
        {
            // If this instruction is a leader and we have accumulated instructions,
            // close the current block and start a new one.
            // Crucial: Only split if the address is DIFFERENT from the last instruction.
            // Some ARM ops (like TBNZ) emit multiple IR ops at the same address!
            if (inst.Address != lastAddress && leaders.Contains(inst.Address) && currentBlockInsts.Count > 0)
            {
                blocks.Add(new IrBasicBlock(blockId++, currentBlockInsts));
                currentBlockInsts = [];
            }

            currentBlockInsts.Add(inst);
            lastAddress = inst.Address;
        }

        // Close the final block
        if (currentBlockInsts.Count > 0)
        {
            blocks.Add(new IrBasicBlock(blockId, currentBlockInsts));
        }

        return blocks;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 3: Edge Connection
    // ═══════════════════════════════════════════════════════════════════════

    private Dictionary<ulong, IrBasicBlock> BuildAddressMap(List<IrBasicBlock> blocks)
    {
        var map = new Dictionary<ulong, IrBasicBlock>();
        foreach (var block in blocks)
        {
            map[block.StartAddress] = block;
        }
        return map;
    }

    private void ConnectEdges(List<IrBasicBlock> blocks, Dictionary<ulong, IrBasicBlock> addressToBlock)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var nextBlock = (i + 1 < blocks.Count) ? blocks[i + 1] : null;

            switch (block.TerminatorKind)
            {
                case IrTerminatorKind.Fallthrough:
                {
                    // No branch — fall through to next sequential block
                    if (nextBlock != null)
                        block.AddSuccessor(nextBlock, IrEdgeKind.Fallthrough);
                    break;
                }

                case IrTerminatorKind.UnconditionalBranch:
                {
                    ulong target = ExtractBranchTarget(block.Terminator);
                    if (target != 0 && addressToBlock.TryGetValue(target, out var targetBlock))
                    {
                        block.AddSuccessor(targetBlock, IrEdgeKind.Unconditional);
                    }
                    else if (target != 0 && nextBlock != null)
                    {
                        // External branch (IL2CPP out-of-line trampoline for class init).
                        // The trampoline executes and branches back to the next instruction.
                        block.AddSuccessor(nextBlock, IrEdgeKind.Unconditional);
                    }
                    break;
                }

                case IrTerminatorKind.ConditionalBranch:
                {
                    // if (cond) goto target — two edges: taken + fallthrough
                    ulong target = ExtractCondBranchTarget(block.Terminator);
                    if (target != 0 && addressToBlock.TryGetValue(target, out var targetBlock))
                        block.AddSuccessor(targetBlock, IrEdgeKind.ConditionalTrue);

                    if (nextBlock != null)
                        block.AddSuccessor(nextBlock, IrEdgeKind.ConditionalFalse);
                    break;
                }

                case IrTerminatorKind.Return:
                {
                    // No successors — method exits here
                    break;
                }

                case IrTerminatorKind.TailCall:
                {
                    // Tail call is a method exit — the target is always an external
                    // function (or a recursive self-call). No intra-method successor.
                    // Creating an edge here would corrupt the CFG with phantom edges
                    // if the external address coincidentally matches an internal block.
                    break;
                }

                case IrTerminatorKind.IndirectBranch:
                {
                    // Indirect branch — target unknown at static analysis time.
                    // Future: resolve from switch table analysis.
                    break;
                }

                case IrTerminatorKind.Unreachable:
                {
                    // Noreturn call (__cxa_throw, etc.) — no successors.
                    break;
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Branch Target Extraction
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extract the branch target address from a Branch or TailCall instruction.
    /// The target is stored as a Label operand in Sources[0].
    /// </summary>
    private static ulong ExtractBranchTarget(IrInstruction inst)
    {
        if (inst.Sources.Length > 0 && inst.Sources[0].Kind == IrOperandKind.Label)
            return (ulong)inst.Sources[0].Value;
        return 0;
    }

    /// <summary>
    /// Extract the branch target address from a ConditionalBranch instruction.
    /// Format: if (Sources[0]) goto Sources[1]
    /// The target label is Sources[1] (or Sources[0] if only one source).
    /// </summary>
    private static ulong ExtractCondBranchTarget(IrInstruction inst)
    {
        // ConditionalBranch: Sources = [condition, target_label]
        if (inst.Sources.Length >= 2 && inst.Sources[1].Kind == IrOperandKind.Label)
            return (ulong)inst.Sources[1].Value;
        // Fallback: single source is the target
        if (inst.Sources.Length >= 1 && inst.Sources[0].Kind == IrOperandKind.Label)
            return (ulong)inst.Sources[0].Value;
        return 0;
    }
}