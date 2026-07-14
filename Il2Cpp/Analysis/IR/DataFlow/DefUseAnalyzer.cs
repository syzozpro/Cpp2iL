using Rosetta.Analysis.IR.SSA;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.IR.DataFlow;

/// <summary>
/// Analyzes def-use chains from SSA context to provide high-level data flow queries.
///
/// Key queries:
///   - What variables are live at a given program point?
///   - Is a variable dead (defined but never used)?
///   - How many uses does a definition have?
///   - What is the single definition of a given use? (SSA guarantee)
///
/// This is the foundation for:
///   - Dead code elimination (unused definitions → removable)
///   - Copy propagation (x1_2 = x0_1 → replace all uses of x1_2 with x0_1)
///   - Type propagation (trace type from definition through uses)
/// </summary>
public sealed class DefUseAnalyzer
{
    private readonly SsaContext _ctx;

    public DefUseAnalyzer(SsaContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Find all dead variables — variables that are defined but never used.
    /// These are candidates for dead code elimination.
    /// Excludes: variables used in phi functions, stores, calls (side effects).
    /// </summary>
    public List<SsaVariable> FindDeadVariables()
    {
        var dead = new List<SsaVariable>();
        foreach (var v in _ctx.AllVariables)
        {
            if (v.IsUndefined) continue;
            if (!_ctx.UseSites.ContainsKey(v) || _ctx.UseSites[v].Count == 0)
                dead.Add(v);
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  DefUseAnalyzer.FindDeadVariables: {dead.Count} dead out of {_ctx.AllVariables.Count}");
        return dead;
    }

    /// <summary>
    /// Count the number of uses for a given SSA variable.
    /// </summary>
    public int UseCount(SsaVariable v)
        => _ctx.UseSites.TryGetValue(v, out var uses) ? uses.Count : 0;

    /// <summary>
    /// Get the definition site of an SSA variable.
    /// Returns (blockId, instructionIndex) or null if not found.
    /// instructionIndex = -1 means it's a phi function.
    /// </summary>
    public (int blockId, int instrIndex)? GetDefinition(SsaVariable v)
        => _ctx.DefSites.TryGetValue(v, out var site) ? site : null;

    /// <summary>
    /// Get all use sites of an SSA variable.
    /// </summary>
    public List<(int blockId, int instrIndex)> GetUses(SsaVariable v)
        => _ctx.UseSites.TryGetValue(v, out var uses) ? uses : [];

    /// <summary>
    /// Compute liveness: which variables are live at the entry of each block.
    /// A variable is live at block entry if it is used in the block before being redefined,
    /// or if it is live at the entry of some successor.
    /// </summary>
    public Dictionary<int, HashSet<SsaVariable>> ComputeLiveIn()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"  DefUseAnalyzer.ComputeLiveIn: {_ctx.Cfg.Blocks.Count} blocks");
        var liveIn = new Dictionary<int, HashSet<SsaVariable>>();
        var liveOut = new Dictionary<int, HashSet<SsaVariable>>();

        foreach (var block in _ctx.Cfg.Blocks)
        {
            liveIn[block.Id] = [];
            liveOut[block.Id] = [];
        }

        // Iterate until fixed point
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in _ctx.Cfg.Blocks)
            {
                // LiveOut = union of LiveIn of all successors
                //         + phi sources flowing from THIS block into successor phis (D1 FIX)
                var newLiveOut = new HashSet<SsaVariable>();
                foreach (var edge in block.Successors)
                {
                    foreach (var v in liveIn[edge.Target.Id])
                        newLiveOut.Add(v);

                    // D1 FIX: Variables feeding into successor Phi nodes are live-out of this block
                    foreach (var phi in _ctx.GetPhis(edge.Target.Id))
                    {
                        foreach (var src in phi.Sources)
                        {
                            if (src.PredecessorBlockId == block.Id)
                                newLiveOut.Add(src.Variable);
                        }
                    }
                }

                // LiveIn = (LiveOut - Defs) ∪ Uses
                var defs = new HashSet<SsaVariable>();
                var uses = new HashSet<SsaVariable>();

                // Phi defs
                foreach (var phi in _ctx.GetPhis(block.Id))
                    defs.Add(phi.Destination);

                // Instruction defs and uses
                foreach (var inst in block.Instructions)
                {
                    // Uses (only if not already defined in this block)
                    for (int s = 0; s < inst.Sources.Length; s++)
                    {
                        var ssaVar = _ctx.GetSource(inst.Address, s);
                        if (ssaVar.HasValue && !defs.Contains(ssaVar.Value))
                            uses.Add(ssaVar.Value);
                    }

                    // D2 FIX: Stack slot uses via StackUseMap
                    if (_ctx.StackUseMap.TryGetValue(inst.Address, out var stackUse))
                    {
                        if (!defs.Contains(stackUse))
                            uses.Add(stackUse);
                    }

                    // Defs
                    var def = _ctx.GetDestination(inst.Address);
                    if (def.HasValue)
                        defs.Add(def.Value);

                    // D2 FIX: Stack slot defs via StackDefMap
                    if (_ctx.StackDefMap.TryGetValue(inst.Address, out var stackDef))
                        defs.Add(stackDef);
                }

                var newLiveIn = new HashSet<SsaVariable>(uses);
                foreach (var v in newLiveOut)
                {
                    if (!defs.Contains(v))
                        newLiveIn.Add(v);
                }

                if (!newLiveIn.SetEquals(liveIn[block.Id]))
                {
                    liveIn[block.Id] = newLiveIn;
                    changed = true;
                }
                liveOut[block.Id] = newLiveOut;
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  ComputeLiveIn done: {liveIn.Values.Sum(s => s.Count)} total live-in entries");
        return liveIn;
    }

    /// <summary>
    /// Get summary statistics for the SSA form.
    /// </summary>
    public SsaStats GetStats()
    {
        var dead = FindDeadVariables();
        return new SsaStats
        {
            TotalVariables = _ctx.VariableCount,
            TotalPhis = _ctx.PhiCount,
            TotalDefs = _ctx.DefSites.Count,
            TotalUses = _ctx.UseSites.Values.Sum(u => u.Count),
            DeadVariables = dead.Count,
        };
    }
}

/// <summary>Summary statistics for SSA analysis.</summary>
public record SsaStats
{
    public int TotalVariables { get; init; }
    public int TotalPhis { get; init; }
    public int TotalDefs { get; init; }
    public int TotalUses { get; init; }
    public int DeadVariables { get; init; }

    public override string ToString() => $"{TotalVariables} vars, {TotalPhis} phis, {TotalDefs} defs, {TotalUses} uses, {DeadVariables} dead";
}
