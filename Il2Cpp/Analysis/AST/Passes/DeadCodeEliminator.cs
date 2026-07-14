using System;
using System.Collections.Generic;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;
using Rosetta.Config;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Statelessly encapsulates Dead Code Elimination and Inlining decisions.
/// </summary>
public static class DeadCodeEliminator
{
    public static bool ShouldEliminate( IrInstruction inst, SsaVariable destVar, ExprNode rhs, DefUseAnalyzer defUse, SsaContext ssa, IrControlFlowGraph cfg, PropagationContext ctx, out bool shouldInline, out string? reason)
    {
        shouldInline = false;
        reason = null;
        
        if(Il2cppConfig.DisableDeadCodeEliminator)
            return false;

        bool hasSideEffects = inst.Opcode is IrOpcode.Call or IrOpcode.IndirectCall or IrOpcode.TailCall;
        if (hasSideEffects && rhs is not ExprCall and not ExprNew)
            hasSideEffects = false;
        if (rhs is ExprAssign assignRhs && assignRhs.Value is ExprNew)
            hasSideEffects = true;

        int effectiveUseCount = CalculateEffectiveUseCount(destVar, defUse, ssa, cfg);

        // Dead code elimination: skip uses=0 variables
        if (effectiveUseCount == 0)
        {
            // Preserve user-visible method calls and critical helpers (they have observable side effects)
            bool isUserVisible = hasSideEffects && rhs is ExprCall ec && (inst.TargetMethodIndex.HasValue || ec.MethodName.Contains("__cxa") || ec.MethodName.Contains("exception_"));
            if (!isUserVisible)
            {
                shouldInline = true;
                reason = "DCE: dead variable (uses=0), eliminated";
                return true;
            }
        }

        // MethodRef values are IL2CPP internal method pointers (MethodInfo*) — never emit as C# statements
        if (rhs is ExprMethodRef)
        {
            shouldInline = true;
            reason = "DCE: MethodRef internal, eliminated";
            return true;
        }

        if (!hasSideEffects && (rhs is ExprVar || rhs is ExprThis))
        {
            shouldInline = true;
            reason = "COPY-PROP/LITERAL inlined";
            return true;
        }

        if (!hasSideEffects && rhs is ExprLiteral litRhs && effectiveUseCount <= 1)
        {
            // Single-use literals: always inline
            shouldInline = true;
            reason = "LITERAL inlined (single-use)";
            return true;
        }

        if (!hasSideEffects && rhs is ExprLiteral litMulti && effectiveUseCount > 1 &&
                 litMulti.Value is not string)
        {
            // Multi-use non-string literals (ints, floats, bools): still inline
            shouldInline = true;
            reason = "LITERAL inlined (multi-use non-string)";
            return true;
        }

        if (effectiveUseCount <= 1 && destVar.IsStackSlot)
        {
            // Stack locals are constructor temporaries consumed by field stores
            shouldInline = true;
            reason = "STACK-LOCAL inlined (single-use ctor temp)";
            return true;
        }

        if (effectiveUseCount == 1 && rhs is ExprNew)
        {
            // ExprNew (like new int[2, 2]) is safe to inline if used exactly once
            shouldInline = true;
            reason = "ExprNew inlined (single-use)";
            return true;
        }

        if (effectiveUseCount != 1)
        {
            // Do not eliminate or inline: emit statement
            shouldInline = false;
            reason = null;
            return false;
        }

        if (hasSideEffects)
        {
            // A side-effecting expression (like a method call) can be inlined if its single use
            // is in the same block, is writing to a class field, and there are no intervening
            // instructions with side effects.
            var defSite = defUse.GetDefinition(destVar);
            var uses = defUse.GetUses(destVar);
            if (defSite.HasValue && uses.Count == 1)
            {
                var (defBlockId, defInstrIdx) = defSite.Value;
                var (useBlockId, useInstrIdx) = uses[0];
                if (defBlockId == useBlockId && defInstrIdx >= 0 && useInstrIdx > defInstrIdx)
                {
                    var block = cfg.FindBlock(defBlockId);
                    if (block != null)
                    {
                        var useInst = block.Instructions[useInstrIdx];
                        if (IsClassFieldStore(useInst))
                        {
                            bool hasInterveningSideEffects = false;
                            for (int i = defInstrIdx + 1; i < useInstrIdx; i++)
                            {
                                if (i >= block.Instructions.Count) break;
                                var otherInst = block.Instructions[i];
                                if (IsSideEffecting(otherInst))
                                {
                                    hasInterveningSideEffects = true;
                                    break;
                                }
                            }

                            if (!hasInterveningSideEffects)
                            {
                                shouldInline = true;
                                reason = "INLINED call to class field (single-use, no intervening side effects)";
                                return true;
                            }
                        }
                    }
                }
            }

            // Do not eliminate or inline: emit statement
            shouldInline = false;
            reason = null;
            return false;
        }

        // Single-use, non-side-effect, non-literal variables are inlined at their point of use
        shouldInline = true;
        bool isPhiSource = ctx.PhiSourceVars.Contains((destVar.VarId, destVar.Version));
        reason = $"INLINED (single-use, phiSrc={isPhiSource})";
        return true;
    }

    private static bool IsClassFieldStore(IrInstruction useInst)
    {
        if (useInst.Opcode is not (IrOpcode.Store or IrOpcode.StoreField))
            return false;

        if (useInst.Annotation == null)
            return false;

        if (Rosetta.Analysis.Utils.StringUtils.IsArrayElementAnnotation(useInst.Annotation))
            return false;

        return useInst.Annotation.StartsWith("->") || 
               useInst.Annotation.Contains("this.") || 
               useInst.Annotation.Contains(":") ||
               useInst.Annotation.Contains(".");
    }

    private static bool IsSideEffecting(IrInstruction otherInst)
    {
        return otherInst.Opcode is 
            IrOpcode.Call or 
            IrOpcode.IndirectCall or 
            IrOpcode.TailCall or 
            IrOpcode.Store or 
            IrOpcode.StoreField or 
            IrOpcode.ClassInit or 
            IrOpcode.RuntimeInit or 
            IrOpcode.WriteBarrier or 
            IrOpcode.NullCheck or 
            IrOpcode.NewObject or 
            IrOpcode.NewArray or 
            IrOpcode.RuntimeHelper or
            IrOpcode.Return or
            IrOpcode.Branch or
            IrOpcode.ConditionalBranch or
            IrOpcode.IndirectBranch;
    }

    private static int CalculateEffectiveUseCount(SsaVariable destVar, DefUseAnalyzer defUse, SsaContext ssa, IrControlFlowGraph cfg)
    {
        int useCount = defUse.UseCount(destVar);
        int effectiveUseCount = useCount;
        if (useCount > 1)
        {
            foreach (var (useBlockId, useInstrIdx) in defUse.GetUses(destVar))
            {
                if (useInstrIdx < 0) continue; // phi use
                var useBlock = cfg.FindBlock(useBlockId);
                if (useBlock != null && useInstrIdx < useBlock.Instructions.Count)
                {
                    var useInst = useBlock.Instructions[useInstrIdx];
                    var useDest = ssa.GetDestination(useInst.Address) ?? 
                                  (ssa.StackDefMap.TryGetValue(useInst.Address, out var sd) ? sd : (SsaVariable?)null);
                    // Consumer is dead if its own SSA def has zero uses
                    if (useDest.HasValue && defUse.UseCount(useDest.Value) == 0)
                    {
                        effectiveUseCount--;
                    }
                }
            }
            if (effectiveUseCount < 0) effectiveUseCount = 0;
        }
        return effectiveUseCount;
    }
}
