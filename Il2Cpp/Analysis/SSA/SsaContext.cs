using Rosetta.Lifter.IR.Nodes;
using Rosetta.Analysis.Utils;

namespace Rosetta.Analysis.IR.SSA;

/// <summary>
/// Per-method SSA context — a non-destructive overlay on the existing IR.
///
/// The original IrInstructions are NOT modified. Instead, this context provides:
///   - SSA variable mappings for each instruction's operands
///   - Phi functions at block entries
///   - Definition/use site tracking
///
/// This design keeps the IR immutable and allows multiple analysis passes
/// to operate independently.
/// </summary>
public sealed class SsaContext
{
    /// <summary>The CFG this SSA was built from.</summary>
    public IrControlFlowGraph Cfg { get; }

    /// <summary>The dominator tree.</summary>
    public DominatorTree DomTree { get; }

    /// <summary>The dominance frontiers.</summary>
    public DominanceFrontier DomFrontier { get; }

    /// <summary>
    /// Phi functions per block. PhiNodes[blockId] = list of phis at that block's entry.
    /// </summary>
    public Dictionary<int, List<PhiFunction>> PhiNodes { get; } = [];

    /// <summary>
    /// Stack slot SSA definitions: Store address → SsaVariable.
    /// Store [SP+N] defines a stack slot variable.
    /// </summary>
    public Dictionary<ulong, SsaVariable> StackDefMap { get; } = [];

    /// <summary>
    /// Stack slot SSA uses: Load address → SsaVariable.
    /// Load [SP+N] uses a stack slot variable.
    /// </summary>
    public Dictionary<ulong, SsaVariable> StackUseMap { get; } = [];

    /// <summary>
    /// Memory base register SSA mapping: (address, sourceIndex) → SsaVariable.
    /// For Memory operands like [x19 + offset], this tracks the SSA version of
    /// the base register (x19). Without this, the base register use is invisible
    /// to SSA, causing incorrect use counts (uses=0) and requiring fallback heuristics.
    /// </summary>
    public Dictionary<(ulong address, int sourceIndex), SsaVariable> MemoryBaseMap { get; } = [];

    /// <summary>
    /// SSA destination mapping: maps (instruction address, operand position) → SsaVariable.
    /// For destinations: key = (address, -1).
    /// For sources: key = (address, source_index).
    /// </summary>
    public Dictionary<(ulong address, int operandIndex), SsaVariable> OperandMap { get; } = [];

    /// <summary>
    /// Where each SSA variable is defined: VarId → list of (blockId, instruction index in block).
    /// </summary>
    public Dictionary<SsaVariable, (int blockId, int instrIndex)> DefSites { get; } = [];

    /// <summary>
    /// Stack slots that have their address taken (escape analysis).
    /// These cannot be safely promoted to SSA or constant-folded.
    /// VarId = 200 + offset.
    /// </summary>
    public HashSet<int> AliasedStackSlots { get; } = [];

    /// <summary>
    /// Where each SSA variable is used: VarId → list of (blockId, instruction index in block).
    /// </summary>
    public Dictionary<SsaVariable, List<(int blockId, int instrIndex)>> UseSites { get; } = [];

    /// <summary>All SSA variables created during construction.</summary>
    public List<SsaVariable> AllVariables { get; } = [];

    /// <summary>Total number of phi functions inserted.</summary>
    public int PhiCount
    {
        get
        {
            int count = 0;
            foreach (var list in PhiNodes.Values) count += list.Count;
            return count;
        }
    }

    /// <summary>Total number of unique SSA variables.</summary>
    public int VariableCount => AllVariables.Count;

    public SsaContext(IrControlFlowGraph cfg, DominatorTree domTree, DominanceFrontier domFrontier)
    {
        Cfg = cfg;
        DomTree = domTree;
        DomFrontier = domFrontier;
    }

    /// <summary>Get the SSA variable for a destination operand at the given address.</summary>
    public SsaVariable? GetDestination(ulong address)
        => OperandMap.TryGetValue((address, -1), out var v) ? v : null;

    /// <summary>Get the SSA variable for a source operand at the given address.</summary>
    public SsaVariable? GetSource(ulong address, int sourceIndex)
        => OperandMap.TryGetValue((address, sourceIndex), out var v) ? v : null;

    /// <summary>Get phi functions for a block (empty list if none).</summary>
    public List<PhiFunction> GetPhis(int blockId)
        => PhiNodes.TryGetValue(blockId, out var phis) ? phis : [];

    // ── Variable extraction from IrOperand ─────────────────────────────────

    /// <summary>
    /// Extract the SSA variable ID from an IrOperand.
    /// Returns -1 for non-register operands or SP (register 31).
    /// GP registers: 0-30, FP registers: 100-131.
    /// </summary>
    public static int ExtractVarId(IrOperand operand)
    {
        return operand.Kind switch
        {
            IrOperandKind.Register when operand.Value >= 0 && operand.Value <= 30 && !ArmUtils.IsStackPointer(operand.Value) => (int)operand.Value,
            IrOperandKind.FpRegister when operand.Value >= 0 && operand.Value <= 31 => 100 + (int)operand.Value,
            _ => -1 // SP, immediates, memory, labels, conditions — not SSA-tracked
        };
    }

    /// <summary>
    /// Extract virtual VarId for a stack slot memory operand.
    /// Returns -1 if the operand is not an SP-relative memory access.
    /// Stack slots: VarId = 200 + offset (unique per SP offset).
    /// </summary>
    public static int ExtractStackVarId(IrOperand memOp)
    {
        if (memOp.Kind == IrOperandKind.Memory && ArmUtils.IsStackPointer(memOp.Value))
            return 200 + (int)memOp.Offset;
        return -1;
    }

    /// <summary>Check if a VarId refers to a stack slot (>= 200).</summary>
    public static bool IsStackVar(int varId) => varId >= 200;

    /// <summary>Get the SP offset from a stack VarId.</summary>
    public static int StackVarOffset(int varId) => varId - 200;

    /// <summary>Extract the bit width from an IrOperand for SSA variable construction.</summary>
    public static byte ExtractBitWidth(IrOperand operand) => operand.BitWidth;
}
