using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Analysis.IR.DataFlow;

/// <summary>
/// Lightweight pre-SSA def-use index for a flat instruction list.
/// Built in O(n), provides O(1) queries for:
///   - "Who last defined register R before instruction i?"
///   - "Is register R read after instruction i before being redefined?"
///   - "Get all uses of the definition at instruction i"
///
/// Uses a composite key (kind, regNum) to distinguish GP registers (x0-x30)
/// from FP registers (s0-s31/d0-d31) that share the same numeric IDs.
/// </summary>
public sealed class DefUseIndex
{
    private readonly Dictionary<(int instrIdx, long regKey), int> _reachingDef;
    private readonly Dictionary<int, List<int>> _uses;
    private readonly long[] _defReg;
    private readonly List<IrInstruction> _insts;

    public DefUseIndex(List<IrInstruction> insts)
    {
        _insts = insts;
        _reachingDef = new Dictionary<(int, long), int>(insts.Count * 2);
        _uses = new Dictionary<int, List<int>>(insts.Count);
        _defReg = new long[insts.Count];
        Build();
    }

    /// <summary>
    /// Get the instruction index that last defined GP register `regNum` before instruction `instrIdx`.
    /// </summary>
    public int GetReachingDef(int instrIdx, long regNum)
        => _reachingDef.TryGetValue((instrIdx, MakeGpKey(regNum)), out int def) ? def : -1;

    /// <summary>Get all instruction indices that use the value defined at `defIdx`.</summary>
    public IReadOnlyList<int> GetUses(int defIdx)
        => _uses.TryGetValue(defIdx, out var uses) ? uses : Array.Empty<int>();

    /// <summary>Returns true if the register defined at `defIdx` has zero uses.</summary>
    public bool IsDead(int defIdx)
        => !_uses.ContainsKey(defIdx) || _uses[defIdx].Count == 0;

    /// <summary>Returns the register number defined by instruction at `instrIdx`, or -1.</summary>
    public long GetDefRegister(int instrIdx)
        => instrIdx >= 0 && instrIdx < _defReg.Length ? _defReg[instrIdx] : -1;

    /// <summary>
    /// Walk backward from `startIdx` through the def chain for GP `regNum`,
    /// collecting all transitive definitions.
    /// </summary>
    public List<int> TraceBackward(int startIdx, long regNum)
    {
        var chain = new List<int>();
        var visited = new HashSet<int>();
        var worklist = new Queue<(int idx, long regKey)>();
        worklist.Enqueue((startIdx, MakeGpKey(regNum)));

        while (worklist.Count > 0)
        {
            var (idx, rk) = worklist.Dequeue();
            if (!_reachingDef.TryGetValue((idx, rk), out int defIdx)) continue;
            if (!visited.Add(defIdx)) continue;

            chain.Add(defIdx);
            var inst = _insts[defIdx];

            if (inst.Opcode is IrOpcode.Load or IrOpcode.LoadAddress
                or IrOpcode.Assign or IrOpcode.LoadImmediate or IrOpcode.Add)
            {
                foreach (var src in inst.Sources)
                {
                    long srcKey = MakeKey(src.Kind, src.Value);
                    if (srcKey >= 0)
                        worklist.Enqueue((defIdx, srcKey));
                }
            }
        }

        return chain;
    }

    // Encode register kind into the key to distinguish GP x8 from FP s8
    private static long MakeGpKey(long regNum) => regNum; // GP: 0..30
    private static long MakeFpKey(long regNum) => regNum + 100; // FP: 100..131
    private static long MakeKey(IrOperandKind kind, long val)
    {
        if (kind == IrOperandKind.Register) return MakeGpKey(val);
        if (kind == IrOperandKind.FpRegister) return MakeFpKey(val);
        if (kind == IrOperandKind.Memory) return MakeGpKey(val); // base register is GP
        return -1;
    }

    private void Build()
    {
        // ── Phase 1: Identify basic block boundaries ──────────────────────
        // Use leader identification (same as IrCfgBuilder) to partition the
        // instruction list. This ensures reaching definitions do NOT cross
        // block boundaries, preventing corrupted data flow across branches.
        var leaders = new HashSet<int> { 0 }; // first instruction is always a leader

        for (int i = 0; i < _insts.Count; i++)
        {
            var inst = _insts[i];
            if (inst.Opcode is IrOpcode.Branch or IrOpcode.TailCall or
                IrOpcode.ConditionalBranch or IrOpcode.Return)
            {
                // Instruction after a terminator starts a new block
                if (i + 1 < _insts.Count)
                    leaders.Add(i + 1);
            }
        }

        // ── Phase 2: Build reaching defs within each block ────────────────
        // Reset lastDef at every block boundary so definitions from one block
        // cannot reach into unrelated blocks (conservative but correct).
        var lastDef = new Dictionary<long, int>();

        for (int i = 0; i < _insts.Count; i++)
        {
            // Reset at block boundaries — cross-block defs are invalid
            // without proper CFG edge analysis
            if (leaders.Contains(i))
                lastDef.Clear();

            var inst = _insts[i];
            _defReg[i] = -1;

            // Record reaching defs for all source operands
            foreach (var src in inst.Sources)
            {
                long key = MakeKey(src.Kind, src.Value);
                if (key >= 0 && lastDef.TryGetValue(key, out int defIdx))
                {
                    _reachingDef[(i, key)] = defIdx;
                    if (!_uses.ContainsKey(defIdx))
                        _uses[defIdx] = new List<int>(2);
                    _uses[defIdx].Add(i);
                }
            }

            // Record definition
            if (inst.Destination.HasValue)
            {
                var dst = inst.Destination.Value;
                long key = MakeKey(dst.Kind, dst.Value);
                if (key >= 0)
                {
                    _defReg[i] = dst.Value;
                    lastDef[key] = i;
                }
            }
        }
    }
}
