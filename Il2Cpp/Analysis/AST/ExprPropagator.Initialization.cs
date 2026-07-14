using System.Collections.Generic;
using System.Linq;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

public sealed partial class ExprPropagator
{
    private void IndexVariableDefinitions(SsaContext ssa, IrControlFlowGraph cfg)
    {
        foreach (var v in ssa.AllVariables)
        {
            _ctx.VarBitWidths[(v.VarId, v.Version)] = v.BitWidth;

            if (v.Version == 0) continue;
            if (!ssa.DefSites.TryGetValue(v, out var defSite)) continue;

            var defBlock = cfg.FindBlock(defSite.blockId);
            if (defBlock == null) continue;

            ulong defAddr;
            if (defSite.instrIndex >= 0 && defSite.instrIndex < defBlock.Instructions.Count)
            {
                defAddr = defBlock.Instructions[defSite.instrIndex].Address;
            }
            else if (defSite.instrIndex < 0)
            {
                // Phi nodes are conceptually at the start of the block.
                defAddr = 0;
            }
            else
            {
                continue;
            }

            if (!_ctx.VarDefsMap.TryGetValue(v.VarId, out var list))
            {
                list = new();
                _ctx.VarDefsMap[v.VarId] = list;
            }
            list.Add((v, defSite.blockId, defAddr));
        }
    }

    private void IndexMemoryAnnotations(IrControlFlowGraph cfg)
    {
        foreach (var block in cfg.Blocks)
        {
            foreach (var inst in block.Instructions)
            {
                if (inst.Annotation == null || inst.Sources.Length == 0)
                    continue;

                var src = inst.Sources[0];
                if (src.Kind == IrOperandKind.Memory)
                    _ctx.MemAnnotations[(src.Value, src.Offset)] = inst.Annotation;
            }
        }
    }

    private void InitializeMultiVersionRegisters()
    {
        var maxVersionPerReg = new Dictionary<int, int>();
        foreach (var v in _ssa.AllVariables)
        {
            maxVersionPerReg.TryGetValue(v.VarId, out int maxVer);
            if (v.Version > maxVer)
                maxVersionPerReg[v.VarId] = v.Version;
        }

        foreach (var (varId, maxVersion) in maxVersionPerReg)
        {
            if (maxVersion <= 1)
                continue;

            _ctx.MultiVersionRegs.Add(varId);
            if (ConsoleReporter.IsTracing)
                ConsoleReporter.Debug($"  multi-version register: VarId={varId} maxVersion={maxVersion}");
        }

        if (ConsoleReporter.IsTracing)
            ConsoleReporter.Trace($"  multi-version regs: {_ctx.MultiVersionRegs.Count} total");
    }

    private void InitializePhiSourceSet()
    {
        _ctx.PhiSourceVars = new HashSet<(int, int)>();
        foreach (var (_, phis) in _ssa.PhiNodes)
        {
            foreach (var phi in phis)
            {
                foreach (var src in phi.Sources)
                    _ctx.PhiSourceVars.Add((src.Variable.VarId, src.Variable.Version));
            }
        }
    }

    private List<int> GetReachableBlockOrder()
    {
        var rpoOrder = _cfg.ReachableReversePostOrder().Select(b => b.Id).ToList();
        if (ConsoleReporter.IsTracing)
            ConsoleReporter.Trace($"  RPO order: [{string.Join(", ", rpoOrder)}] ({rpoOrder.Count} blocks)");
        return rpoOrder;
    }
}
