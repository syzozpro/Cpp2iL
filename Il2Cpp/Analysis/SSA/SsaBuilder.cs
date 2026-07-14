using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;
using Rosetta.Analysis.Utils;

namespace Rosetta.Analysis.IR.SSA;

/// <summary>
/// Builds SSA form for an IR control flow graph.
///
/// Algorithm (Cytron et al., 1991):
///   Phase 1: Compute dominator tree and dominance frontiers.
///   Phase 2: Insert phi functions at iterated dominance frontiers for each variable.
///   Phase 3: Rename variables by walking the dominator tree in pre-order.
///
/// Extended with mem2reg: Stack slots [SP+N] are promoted to virtual SSA variables
/// (VarId = 200 + offset). This creates Phi nodes at loop headers for stack-based
/// induction variables, enabling Phi-cycle-based loop detection.
/// </summary>
public sealed class SsaBuilder
{
    private struct SsaShape { public byte BitWidth, ElementWidth, ElementCount; }
    /// <summary>Cached shapes per variable, precomputed once per CFG.</summary>
    private Dictionary<int, SsaShape> _shapeCache = new();
    /// <summary>
    /// Build SSA form for the given CFG.
    /// Returns null if the CFG is empty.
    /// </summary>
    public SsaContext? Build(IrControlFlowGraph cfg)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"SsaBuilder.Build() START: {cfg.Blocks.Count} blocks, {cfg.Method.MethodName}");
        if (cfg.Blocks.Count == 0)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  SsaBuilder: empty CFG, returning null");
            return null;
        }

        // Phase 1: Dominator tree + dominance frontiers
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"  SSA Phase 1: computing dominator tree...");
        var domTree = DominatorTree.Build(cfg);
        var domFrontier = DominanceFrontier.Build(cfg, domTree);
        var ctx = new SsaContext(cfg, domTree, domFrontier);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  SSA Phase 1 done: {cfg.Blocks.Count} blocks in dom tree");

        // Phase 2: Find all variables and their definition sites, insert phis
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"  SSA Phase 2: finding variable definitions + inserting phis...");
        var varDefs = FindVariableDefinitions(cfg, ctx);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  SSA Phase 2a: {varDefs.Count} unique variables found");
        InsertPhiFunctions(cfg, ctx, domFrontier, varDefs);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  SSA Phase 2b: {ctx.PhiCount} phi functions inserted");

        // Phase 2c: Precompute shapes for all variables (avoids O(V×B×I) in rename)
        _shapeCache = PrecomputeShapes(cfg, varDefs);

        // Phase 3: Rename variables
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"  SSA Phase 3: renaming variables...");
        RenameVariables(cfg, ctx, domTree, varDefs);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  SSA Phase 3 done: {ctx.VariableCount} SSA variables");

        // Phase 4: Build def-use chains
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"  SSA Phase 4: building def-use chains...");
        BuildDefUseChains(cfg, ctx);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  SSA Phase 4 done: {ctx.DefSites.Count} defs, {ctx.UseSites.Count} use-sets");

        // Phase 5: Remove dead phis
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"  SSA Phase 5: eliminating dead phis...");
        int phisBefore = ctx.PhiCount;
        EliminateDeadPhis(ctx);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  SSA Phase 5 done: {phisBefore - ctx.PhiCount} dead phis removed, {ctx.PhiCount} remaining");

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"SsaBuilder.Build() END: {ctx.VariableCount} vars, {ctx.PhiCount} phis");
        return ctx;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 2: Find Variable Definitions
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scan all blocks and find which variables (registers + stack slots) are defined.
    /// Returns: varId → set of blockIds where that variable is defined.
    ///
    private Dictionary<int, HashSet<int>> FindVariableDefinitions(IrControlFlowGraph cfg, SsaContext ctx)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    FindVariableDefinitions: scanning {cfg.Blocks.Count} blocks");
        var varDefs = new Dictionary<int, HashSet<int>>();

        // ── Escape analysis: detect address-taken stack slots ─────────────
        var aliasedStackSlots = new HashSet<int>();
        
        var stackSlotLoads = new Dictionary<int, int>();
        var stackSlotAddressTakes = new Dictionary<int, int>();
        var structBoxPtrSlots = new HashSet<int>();

        // Pass 1: Count loads and address-takes for stack slots
        foreach (var block in cfg.Blocks)
        {
            foreach (var inst in block.Instructions)
            {
                // Count Loads
                if (inst.Opcode == IrOpcode.Load && inst.Sources.Length >= 1)
                {
                    int stackVarId = SsaContext.ExtractStackVarId(inst.Sources[0]);
                    if (stackVarId >= 0)
                    {
                        stackSlotLoads[stackVarId] = stackSlotLoads.GetValueOrDefault(stackVarId) + 1;
                    }
                }

                // Count Address Takes
                if (inst.Opcode == IrOpcode.Add &&
                    inst.Sources.Length >= 2 &&
                    inst.Sources[0].Kind == IrOperandKind.Register &&
                    ArmUtils.IsStackPointer(inst.Sources[0].Value) &&
                    inst.Sources[1].Kind == IrOperandKind.Immediate)
                {
                    int offset = (int)inst.Sources[1].Value;
                    int stackVarId = 200 + offset;
                    
                    if (inst.Annotation == "struct_box_ptr")
                        structBoxPtrSlots.Add(stackVarId);
                    else
                        stackSlotAddressTakes[stackVarId] = stackSlotAddressTakes.GetValueOrDefault(stackVarId) + 1;
                }
            }
        }
        
        // Pass 2: Determine which are truly aliased
        foreach (var kvp in stackSlotAddressTakes)
        {
            int stackVarId = kvp.Key;
            int takes = kvp.Value;
            int loads = stackSlotLoads.GetValueOrDefault(stackVarId);
            
            // A stack slot is a compiler temporary if it is never loaded, and its address is taken exactly once.
            // If it is loaded, or address taken multiple times, or is a struct_box_ptr, it is ALIASED.
            if (loads > 0 || takes > 1 || structBoxPtrSlots.Contains(stackVarId))
            {
                aliasedStackSlots.Add(stackVarId);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      escape: SP+{stackVarId - 200} (varId={stackVarId}) is aliased (loads={loads}, takes={takes})");
            }
            else
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      escape: SP+{stackVarId - 200} (varId={stackVarId}) is compiler temporary (loads=0, takes=1), NOT aliased");
            }
        }

        foreach (int aliased in aliasedStackSlots)
            ctx.AliasedStackSlots.Add(aliased);
            
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"    aliased stack slots: {aliasedStackSlots.Count}");

        // ── Collect variable definitions ──────────────────────────────────
        foreach (var block in cfg.Blocks)
        {
            foreach (var inst in block.Instructions)
            {
                if (inst.Destination.HasValue)
                {
                    int varId = SsaContext.ExtractVarId(inst.Destination.Value);
                    if (varId >= 0)
                    {
                        if (!varDefs.ContainsKey(varId))
                            varDefs[varId] = [];
                        varDefs[varId].Add(block.Id);
                    }
                }

                // Caller-saved register clobbers: treat the call as an implicit
                // def-site for every clobbered register that is defined elsewhere
                // in the method (to avoid creating phi nodes for unused registers).
                if (inst.ClobberedRegisters != null)
                {
                    int explicitDest = inst.Destination.HasValue
                        ? SsaContext.ExtractVarId(inst.Destination.Value) : -1;
                    foreach (int reg in inst.ClobberedRegisters)
                    {
                        if (reg == explicitDest) continue;
                        if (varDefs.ContainsKey(reg))
                            varDefs[reg].Add(block.Id);
                    }
                }

                if (inst.Opcode == IrOpcode.Store && inst.Sources.Length >= 2)
                {
                    int stackVarId = SsaContext.ExtractStackVarId(inst.Sources[0]);
                    if (stackVarId >= 0 && !aliasedStackSlots.Contains(stackVarId))
                    {
                        if (!varDefs.ContainsKey(stackVarId))
                            varDefs[stackVarId] = [];
                        varDefs[stackVarId].Add(block.Id);
                    }
                }
            }
        }

        if (ConsoleReporter.IsTracing)
        {
            int defSiteCount = 0;
            foreach (var s in varDefs.Values) defSiteCount += s.Count;
            ConsoleReporter.Trace($"    FindVariableDefinitions: {varDefs.Count} variables, {defSiteCount} def-sites");
        }
        return varDefs;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 2: Phi Insertion
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// For each variable, place phi functions at the iterated dominance frontier
    /// of the blocks where that variable is defined.
    /// </summary>
    private void InsertPhiFunctions(
        IrControlFlowGraph cfg,
        SsaContext ctx,
        DominanceFrontier domFrontier,
        Dictionary<int, HashSet<int>> varDefs)
    {
        int insertedCount = 0;
        foreach (var (varId, defBlocks) in varDefs)
        {
            var phiBlocks = domFrontier.IteratedFrontier(defBlocks);

            foreach (int blockId in phiBlocks)
            {
                var block = cfg.FindBlock(blockId);
                if (block == null || block.Predecessors.Count < 2)
                    continue;

                if (!ctx.PhiNodes.ContainsKey(blockId))
                    ctx.PhiNodes[blockId] = [];

                bool alreadyExists = ctx.PhiNodes[blockId].Any(p => p.VarId == varId);
                if (!alreadyExists)
                {
                    ctx.PhiNodes[blockId].Add(new PhiFunction(blockId, varId));
                    insertedCount++;
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      phi inserted: varId={varId} at block {blockId} (preds={block.Predecessors.Count})");
                }
            }
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    InsertPhiFunctions: {insertedCount} phis placed");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 3: Variable Renaming (DFS on dominator tree)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rename all variable references to SSA-versioned names.
    /// Walks the dominator tree in pre-order, maintaining a stack of current
    /// variable versions.
    /// </summary>
    private void RenameVariables(
        IrControlFlowGraph cfg,
        SsaContext ctx,
        DominatorTree domTree,
        Dictionary<int, HashSet<int>> varDefs)
    {
        // Version counter per variable (monotonically increasing)
        var versionCounter = new Dictionary<int, int>();

        // Stack of current version for each variable
        var versionStack = new Dictionary<int, Stack<int>>();

        // Initialize all known variables (registers + stack slots)
        var allVarIds = new HashSet<int>(varDefs.Keys);

        // Also collect variables that are USED (might not be defined in this method — parameters)
        foreach (var block in cfg.Blocks)
        {
            foreach (var inst in block.Instructions)
            {
                // Register uses
                foreach (var src in inst.Sources)
                {
                    int varId = SsaContext.ExtractVarId(src);
                    if (varId >= 0) allVarIds.Add(varId);
                }

                // Stack slot uses: Load [SP+N]
                if (inst.Opcode == IrOpcode.Load && inst.Sources.Length >= 1)
                {
                    int stackVarId = SsaContext.ExtractStackVarId(inst.Sources[0]);
                    if (stackVarId >= 0 && !ctx.AliasedStackSlots.Contains(stackVarId)) allVarIds.Add(stackVarId);
                }
            }
        }

        foreach (int varId in allVarIds)
        {
            versionCounter[varId] = 0;
            versionStack[varId] = new Stack<int>();
            versionStack[varId].Push(0); // version 0 = initial (entry/parameter)
        }

        // DFS pre-order on dominator tree
        RenameBlock(cfg, ctx, domTree, cfg.EntryBlock.Id, versionCounter, versionStack);
    }

    private void RenameBlock(
        IrControlFlowGraph cfg,
        SsaContext ctx,
        DominatorTree domTree,
        int blockId,
        Dictionary<int, int> versionCounter,
        Dictionary<int, Stack<int>> versionStack,
        int depth = 0)
    {
        // Guard against StackOverflow on deeply nested CFGs (threadpool stacks = 1MB)
        if (depth > 200) return;
        
        var block = cfg.FindBlock(blockId);
        if (block == null) return;

        // Track how many versions we pushed in this block (for cleanup on backtrack)
        var pushCounts = new Dictionary<int, int>();

        RenamePhiNodes(blockId, ctx, versionCounter, versionStack, pushCounts);
        RenameInstructionOperands(block, ctx, versionCounter, versionStack, pushCounts);
        UpdateSuccessorPhis(block, ctx, versionStack);

        // ── Recurse into dominator tree children ───────────────────────────
        foreach (int childId in domTree.DomChildren[blockId])
        {
            RenameBlock(cfg, ctx, domTree, childId, versionCounter, versionStack, depth + 1);
        }

        // ── Pop versions pushed in this block (backtrack) ──────────────────
        foreach (var (varId, count) in pushCounts)
        {
            for (int i = 0; i < count; i++)
                versionStack[varId].Pop();
        }
    }

    private void RenamePhiNodes(int blockId, SsaContext ctx, Dictionary<int, int> versionCounter, Dictionary<int, Stack<int>> versionStack, Dictionary<int, int> pushCounts)
    {
        foreach (var phi in ctx.GetPhis(blockId))
        {
            int newVersion = ++versionCounter[phi.VarId];
            var shape = _shapeCache.TryGetValue(phi.VarId, out var sh) ? sh : new SsaShape { BitWidth = 64 };
            var ssaVar = new SsaVariable(phi.VarId, newVersion, shape.BitWidth, shape.ElementWidth, shape.ElementCount);

            phi.Destination = ssaVar;
            ctx.AllVariables.Add(ssaVar);

            versionStack[phi.VarId].Push(newVersion);
            pushCounts[phi.VarId] = pushCounts.GetValueOrDefault(phi.VarId) + 1;
        }
    }

    private void RenameInstructionOperands(IrBasicBlock block, SsaContext ctx, Dictionary<int, int> versionCounter, Dictionary<int, Stack<int>> versionStack, Dictionary<int, int> pushCounts)
    {
        for (int i = 0; i < block.Instructions.Count; i++)
        {
            var inst = block.Instructions[i];

            RenameInstructionSources(inst, ctx, versionStack);
            RenameInstructionDestination(inst, ctx, versionCounter, versionStack, pushCounts);
            RenameInstructionClobbers(inst, ctx, versionCounter, versionStack, pushCounts);
        }
    }

    private void RenameInstructionSources(IrInstruction inst, SsaContext ctx, Dictionary<int, Stack<int>> versionStack)
    {
        for (int s = 0; s < inst.Sources.Length; s++)
        {
            int varId = SsaContext.ExtractVarId(inst.Sources[s]);
            if (varId >= 0)
            {
                if (versionStack.ContainsKey(varId))
                {
                    int currentVersion = versionStack[varId].Peek();
                    var shape = _shapeCache.TryGetValue(varId, out var sh) ? sh : new SsaShape { BitWidth = 64 };
                    ctx.OperandMap[(inst.Address, s)] = new SsaVariable(varId, currentVersion, shape.BitWidth, shape.ElementWidth, shape.ElementCount);
                }
                continue;
            }

            var src = inst.Sources[s];
            if (src.Kind == IrOperandKind.Memory && !ArmUtils.IsStackPointer(src.Value)) // skip SP
            {
                int baseVarId = (int)src.Value;
                if (baseVarId >= 0 && baseVarId <= 30 && versionStack.ContainsKey(baseVarId))
                {
                    int currentVersion = versionStack[baseVarId].Peek();
                    ctx.MemoryBaseMap[(inst.Address, s)] = new SsaVariable(baseVarId, currentVersion, 64);
                }
            }
        }

        if (inst.Opcode == IrOpcode.Load && inst.Sources.Length >= 1)
        {
            int stackVarId = SsaContext.ExtractStackVarId(inst.Sources[0]);
            if (stackVarId >= 0 && versionStack.ContainsKey(stackVarId))
            {
                int currentVersion = versionStack[stackVarId].Peek();
                var shape = _shapeCache.TryGetValue(stackVarId, out var sh) ? sh : new SsaShape { BitWidth = 64 };
                ctx.StackUseMap[inst.Address] = new SsaVariable(stackVarId, currentVersion, shape.BitWidth, shape.ElementWidth, shape.ElementCount);
            }
        }
    }

    private void RenameInstructionDestination(IrInstruction inst, SsaContext ctx, Dictionary<int, int> versionCounter, Dictionary<int, Stack<int>> versionStack, Dictionary<int, int> pushCounts)
    {
        if (inst.Destination.HasValue)
        {
            int varId = SsaContext.ExtractVarId(inst.Destination.Value);
            if (varId >= 0 && versionCounter.ContainsKey(varId))
            {
                int newVersion = ++versionCounter[varId];
                var shape = _shapeCache.TryGetValue(varId, out var sh) ? sh : new SsaShape { BitWidth = 64 };
                var ssaVar = new SsaVariable(varId, newVersion, shape.BitWidth, shape.ElementWidth, shape.ElementCount);
                ctx.OperandMap[(inst.Address, -1)] = ssaVar;
                ctx.AllVariables.Add(ssaVar);

                versionStack[varId].Push(newVersion);
                pushCounts[varId] = pushCounts.GetValueOrDefault(varId) + 1;
            }
        }

        if (inst.Opcode == IrOpcode.Store && inst.Sources.Length >= 2)
        {
            int stackVarId = SsaContext.ExtractStackVarId(inst.Sources[0]);
            if (stackVarId >= 0 && versionCounter.ContainsKey(stackVarId))
            {
                int newVersion = ++versionCounter[stackVarId];
                var shape = _shapeCache.TryGetValue(stackVarId, out var sh) ? sh : new SsaShape { BitWidth = 64 };
                var ssaVar = new SsaVariable(stackVarId, newVersion, shape.BitWidth, shape.ElementWidth, shape.ElementCount);
                ctx.StackDefMap[inst.Address] = ssaVar;
                ctx.AllVariables.Add(ssaVar);

                versionStack[stackVarId].Push(newVersion);
                pushCounts[stackVarId] = pushCounts.GetValueOrDefault(stackVarId) + 1;
            }
        }
    }

    private void RenameInstructionClobbers(IrInstruction inst, SsaContext ctx, Dictionary<int, int> versionCounter, Dictionary<int, Stack<int>> versionStack, Dictionary<int, int> pushCounts)
    {
        if (inst.ClobberedRegisters != null)
        {
            int explicitDest = inst.Destination.HasValue
                ? SsaContext.ExtractVarId(inst.Destination.Value) : -1;
            foreach (int reg in inst.ClobberedRegisters)
            {
                if (reg == explicitDest) continue;
                if (!versionCounter.ContainsKey(reg)) continue;

                int newVersion = ++versionCounter[reg];
                var shape = _shapeCache.TryGetValue(reg, out var sh) ? sh : new SsaShape { BitWidth = 64 };
                var ssaVar = new SsaVariable(reg, newVersion, shape.BitWidth, shape.ElementWidth, shape.ElementCount);
                ctx.AllVariables.Add(ssaVar);
                ctx.OperandMap[(inst.Address, -(reg + 2))] = ssaVar;

                versionStack[reg].Push(newVersion);
                pushCounts[reg] = pushCounts.GetValueOrDefault(reg) + 1;
            }
        }
    }

    private void UpdateSuccessorPhis(IrBasicBlock block, SsaContext ctx, Dictionary<int, Stack<int>> versionStack)
    {
        foreach (var edge in block.Successors)
        {
            foreach (var phi in ctx.GetPhis(edge.Target.Id))
            {
                if (!versionStack.ContainsKey(phi.VarId)) continue;
                int currentVersion = versionStack[phi.VarId].Peek();
                var shape = _shapeCache.TryGetValue(phi.VarId, out var sh) ? sh : new SsaShape { BitWidth = 64 };
                var ssaVar = new SsaVariable(phi.VarId, currentVersion, shape.BitWidth, shape.ElementWidth, shape.ElementCount);
                phi.Sources.Add(new PhiSource(ssaVar, block.Id));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 4: Def-Use Chains
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildDefUseChains(IrControlFlowGraph cfg, SsaContext ctx)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    BuildDefUseChains: scanning {cfg.Blocks.Count} blocks");
        foreach (var block in cfg.Blocks)
        {
            // Phi definitions
            foreach (var phi in ctx.GetPhis(block.Id))
            {
                ctx.DefSites[phi.Destination] = (block.Id, -1); // -1 = phi

                // Phi sources are uses
                foreach (var src in phi.Sources)
                {
                    if (!ctx.UseSites.ContainsKey(src.Variable))
                        ctx.UseSites[src.Variable] = [];
                    ctx.UseSites[src.Variable].Add((block.Id, -1));
                }
            }

            // Instruction definitions and uses
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                var inst = block.Instructions[i];

                // Register definition
                var def = ctx.GetDestination(inst.Address);
                if (def.HasValue)
                    ctx.DefSites[def.Value] = (block.Id, i);

                // Stack slot definition
                if (ctx.StackDefMap.TryGetValue(inst.Address, out var stackDef))
                    ctx.DefSites[stackDef] = (block.Id, i);

                // Register uses
                for (int s = 0; s < inst.Sources.Length; s++)
                {
                    var use = ctx.GetSource(inst.Address, s);
                    if (use.HasValue)
                    {
                        if (!ctx.UseSites.ContainsKey(use.Value))
                            ctx.UseSites[use.Value] = [];
                        ctx.UseSites[use.Value].Add((block.Id, i));
                    }
                }

                // Stack slot use
                if (ctx.StackUseMap.TryGetValue(inst.Address, out var stackUse))
                {
                    if (!ctx.UseSites.ContainsKey(stackUse))
                        ctx.UseSites[stackUse] = [];
                    ctx.UseSites[stackUse].Add((block.Id, i));
                }

                // Memory base register uses
                for (int s = 0; s < inst.Sources.Length; s++)
                {
                    if (ctx.MemoryBaseMap.TryGetValue((inst.Address, s), out var memBase))
                    {
                        if (!ctx.UseSites.ContainsKey(memBase))
                            ctx.UseSites[memBase] = [];
                        ctx.UseSites[memBase].Add((block.Id, i));
                    }
                }
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Precompute shapes for ALL variables in a single pass over the CFG.
    /// This replaces per-variable guessing which was O(V × blocks × instrs).
    /// </summary>
    private static Dictionary<int, SsaShape> PrecomputeShapes(
        IrControlFlowGraph cfg,
        Dictionary<int, HashSet<int>> varDefs)
    {
        var shapes = new Dictionary<int, SsaShape>();

        foreach (var block in cfg.Blocks)
        {
            foreach (var inst in block.Instructions)
            {
                // Register definitions
                if (inst.Destination.HasValue)
                {
                    int varId = SsaContext.ExtractVarId(inst.Destination.Value);
                    if (varId >= 0)
                    {
                        byte w = inst.Destination.Value.BitWidth;
                        byte ew = inst.Destination.Value.ElementWidth;
                        byte ec = inst.Destination.Value.ElementCount;
                        if (!shapes.TryGetValue(varId, out var cur) || w > cur.BitWidth
                            || (w == cur.BitWidth && ec > cur.ElementCount))
                            shapes[varId] = new SsaShape { BitWidth = w, ElementWidth = ew, ElementCount = ec };
                    }
                }

                // Stack slot definitions
                if (inst.Opcode == IrOpcode.Store && inst.Sources.Length >= 2)
                {
                    int svId = SsaContext.ExtractStackVarId(inst.Sources[0]);
                    if (svId >= 0)
                    {
                        // Sources[1] is the value being stored; Sources[0] is the memory address
                        byte w = inst.Sources[1].BitWidth;
                        byte ew = inst.Sources[1].ElementWidth;
                        byte ec = inst.Sources[1].ElementCount;
                        if (!shapes.TryGetValue(svId, out var cur) || w > cur.BitWidth)
                            shapes[svId] = new SsaShape { BitWidth = w, ElementWidth = ew, ElementCount = ec };
                    }
                }
            }
        }

        return shapes;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Phase 5: Dead Phi Elimination
    // ═══════════════════════════════════════════════════════════════════════

    private void EliminateDeadPhis(SsaContext ctx)
    {
        int totalRemoved = 0;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (blockId, phis) in ctx.PhiNodes)
            {
                for (int i = phis.Count - 1; i >= 0; i--)
                {
                    if (!IsPhiLive(phis[i].Destination, ctx, []))
                    {
                        var deadPhi = phis[i];
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      dead phi: varId={deadPhi.VarId} dest={deadPhi.Destination.Name} v{deadPhi.Destination.Version} block={blockId}");
                        foreach (var src in deadPhi.Sources)
                        {
                            if (ctx.UseSites.TryGetValue(src.Variable, out var sites))
                            {
                                for (int j = sites.Count - 1; j >= 0; j--)
                                {
                                    if (sites[j].blockId == blockId && sites[j].instrIndex == -1)
                                    {
                                        sites.RemoveAt(j);
                                        break;
                                    }
                                }
                            }
                        }
                        phis.RemoveAt(i);
                        totalRemoved++;
                        changed = true;
                    }
                }
            }
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    EliminateDeadPhis: removed {totalRemoved} dead phis");
    }

    /// <summary>
    /// Check if a phi destination is live: used by a real instruction,
    /// or transitively used by another live phi.
    /// S3 FIX: Uses UseSites map for O(1) lookup instead of scanning all phis.
    /// </summary>
    private static bool IsPhiLive(SsaVariable dest, SsaContext ctx, HashSet<SsaVariable> visited)
    {
        if (!visited.Add(dest)) return false;
        if (!ctx.UseSites.TryGetValue(dest, out var uses) || uses.Count == 0) return false;

        foreach (var (useBlock, idx) in uses)
        {
            if (idx >= 0) return true; // Used by a real instruction — definitely live

            // It's used by a phi in useBlock. Look up exactly that block's phis.
            if (ctx.PhiNodes.TryGetValue(useBlock, out var blockPhis))
            {
                foreach (var phi in blockPhis)
                {
                    if (phi.Sources.Any(s => s.Variable.VarId == dest.VarId && s.Variable.Version == dest.Version))
                    {
                        if (IsPhiLive(phi.Destination, ctx, visited)) return true;
                    }
                }
            }
        }
        return false;
    }
}
