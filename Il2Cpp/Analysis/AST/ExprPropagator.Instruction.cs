using System.Collections.Generic;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;
using Rosetta.Analysis.Utils;


namespace Rosetta.Analysis.AST;

public sealed partial class ExprPropagator
{
    private void ProcessInstruction(IrInstruction inst, int blockId, int instrIdx, List<ExprStatement> stmts)
    {
        _ctx.CurrentBlockId = blockId;
        if (inst.Opcode == IrOpcode.Unknown)
        {
            System.Console.WriteLine($"[PROPAGATOR-UNKNOWN] Silently omitting unknown instruction in method {_method.MethodName} at 0x{inst.Address:X}: {inst.Annotation}");
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    [DEBUG] ProcessInstruction START! block={blockId} idx={instrIdx} opcode={inst.Opcode} addr=0x{inst.Address:X} ann=\"{inst.Annotation ?? "null"}\"");
        if (ConsoleReporter.IsTracing)
            ConsoleReporter.Trace($"    ProcessInstruction: block={blockId} idx={instrIdx} opcode={inst.Opcode} addr=0x{inst.Address:X} ann=\"{inst.Annotation ?? "null"}\"");

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] Building right-hand side expression...");
        ExprNode? rhs = BuildExpression(inst);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] Finished building rhs: {(rhs == null ? "null" : rhs.GetType().Name)}");
        var destSsa = _ssa.GetDestination(inst.Address);

        DetectFramePointerSetup(inst, destSsa);

        if (inst.Opcode == IrOpcode.Store)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] ProcessInstruction -> Routing to ProcessStoreInstruction");
            ProcessStoreInstruction(inst, stmts);
            return;
        }

        if (inst.Opcode == IrOpcode.Compare || inst.Opcode == IrOpcode.FCompare)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] ProcessInstruction -> Routing to ProcessCompareInstruction");
            ProcessCompareInstruction(inst, destSsa, rhs);
            return;
        }

        if (inst.Opcode is IrOpcode.ConditionalBranch or IrOpcode.Branch)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] ProcessInstruction -> Branch skipped");
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      branch: {inst.Opcode}");
            return;
        }

        if (inst.Opcode == IrOpcode.Return)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] ProcessInstruction -> Routing to ProcessReturnInstruction");
            ProcessReturnInstruction(inst, stmts);
            return;
        }

        if (inst.Opcode == IrOpcode.TailCall)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] ProcessInstruction -> Routing to ProcessTailCallInstruction");
            ProcessTailCallInstruction(inst, rhs, stmts);
            return;
        }

        if (destSsa.HasValue && rhs != null)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] ProcessInstruction -> Routing to ProcessDestinationExpression");
            ProcessDestinationExpression(inst, destSsa.Value, rhs, stmts);
        }
        else if (rhs != null)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] ProcessInstruction -> Routing to ProcessVoidExpression");
            ProcessVoidExpression(inst, rhs, stmts);
        }
        else
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] ProcessInstruction -> Routing to ProcessNoOpInstruction");
            ProcessNoOpInstruction(inst, destSsa);
        }
    }

    private void DetectFramePointerSetup(IrInstruction inst, SsaVariable? destSsa)
    {
        if (inst.Opcode == IrOpcode.Add && destSsa.HasValue &&
            destSsa.Value.VarId == 29 && !destSsa.Value.IsFloat &&
            inst.Sources.Length >= 2 &&
            inst.Sources[0].Kind == IrOperandKind.Register && ArmUtils.IsStackPointer(inst.Sources[0].Value) &&
            inst.Sources[1].Kind == IrOperandKind.Immediate)
        {
            _ctx.FpSpOffset = inst.Sources[1].Value;
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      FP setup detected: x29 = SP + {_ctx.FpSpOffset}");
        }
    }

    private void ProcessStoreInstruction(IrInstruction inst, List<ExprStatement> stmts)
    {
        var storeExpr = BuildStore(inst);
        bool isStackStore = TryTrackStackStore(inst, out var stackDefSsa);

        if (storeExpr == null)
            return;

        int stackUseCount = isStackStore ? _defUse.UseCount(stackDefSsa) : -1;
        bool skipSpStore = isStackStore && stackUseCount == 1;
        if (skipSpStore)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug("      store -> SKIP (stack temp consumed at use site)");
            return;
        }

        bool isFirstStore = storeExpr is ExprAssign { Target: ExprSpSlot spSlot } &&
            _declaredSpSlots.Add(spSlot.Offset);

        stmts.Add(new ExprStatement { Expr = storeExpr, Inst = inst, IsDeclaration = isFirstStore });
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      store -> {storeExpr.Emit()} (decl={isFirstStore})");
    }

    private bool TryTrackStackStore(IrInstruction inst, out SsaVariable stackDefSsa)
    {
        if (_ssa.StackDefMap.TryGetValue(inst.Address, out stackDefSsa) && inst.Sources.Length >= 2)
        {
            ExprMap[stackDefSsa] = GetSourceExpr(inst, 1);
            return true;
        }

        return false;
    }

    private void ProcessCompareInstruction(IrInstruction inst, SsaVariable? destSsa, ExprNode? rhs)
    {
        if (!destSsa.HasValue)
            return;

        ExprMap[destSsa.Value] = rhs ?? new ExprLiteral("?cmp");
        if (ConsoleReporter.IsTracing)
            ConsoleReporter.Debug($"      compare: {destSsa.Value.Name} v{destSsa.Value.Version} = {ExprMap[destSsa.Value].Emit()}");
    }

    private void ProcessReturnInstruction(IrInstruction inst, List<ExprStatement> stmts)
    {
        if (inst.Sources.Length == 0)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug("      return: void");
            return;
        }

        var retExpr = GetSourceExpr(inst, 0);
        stmts.Add(new ExprStatement { Expr = retExpr, Inst = inst, IsReturn = true });
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      return: {retExpr.Emit()}");
    }

    private void ProcessTailCallInstruction(IrInstruction inst, ExprNode? rhs, List<ExprStatement> stmts)
    {
        if (rhs == null)
            return;

        bool isReturn = _method.ReturnType != "void";
        stmts.Add(new ExprStatement { Expr = rhs, Inst = inst, IsReturn = isReturn });
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      tailcall: {rhs.Emit()}");
    }

    private bool IsArrayExpression(ExprNode expr, out int size)
    {
        if (expr == null) 
        {
            size = -1;
            return false;
        }

        return ArrayLiteralRecovery.IsArray(expr, out size);
    }

    private bool IsCollectionExpression(ExprNode expr, out bool IsInisilized)
    {
        IsInisilized = false;
        return false;
    }

    private void ProcessDestinationExpression(IrInstruction inst, SsaVariable destSsa, ExprNode rhs, List<ExprStatement> stmts)
    {
        if (IsCollectionExpression(rhs, out bool isCollInit))
        {

        }
        if (IsArrayExpression(rhs, out int arraySize))
        {
            destSsa.IsArray = true;
            destSsa.SizeArray = arraySize;
        }

        ExprMap[destSsa] = rhs;

        int useCount = _defUse.UseCount(destSsa);
        bool hasSideEffects = HasEmittableSideEffects(inst, rhs);
        int effectiveUseCount = GetEffectiveUseCount(destSsa, useCount);

        if (ConsoleReporter.IsTracing)
            ConsoleReporter.Debug($"      dest={destSsa.Name} v{destSsa.Version} useCount={useCount} sideEffect={hasSideEffects} rhs={rhs.GetType().Name}({rhs.Emit()})");

        if (DeadCodeEliminator.ShouldEliminate(inst, destSsa, rhs, _defUse, _ssa, _cfg, _ctx, out bool shouldInline, out string? reason))
        {
            if (shouldInline && destSsa.ElementCount > 1 && rhs is ExprLiteral lit && lit.Value is not float && lit.Value is not double)
                shouldInline = false;

            if (shouldInline)
                Inlined.Add(destSsa);

            if (ConsoleReporter.IsTracing && reason != null)
                ConsoleReporter.Debug($"      -> {reason}");
            return;
        }

        var target = MakeVarExpr(destSsa);
        if (target.InferredType == null && rhs.InferredType != null)
        {
            target.InferredType = rhs.InferredType;
        }

        bool isFirstDef = IsFirstDefinition(destSsa);
        stmts.Add(new ExprStatement
        {
            Expr = new ExprAssign(target, rhs),
            Inst = inst,
            IsDeclaration = isFirstDef,
            SsaVar = destSsa
        });
        if (ConsoleReporter.IsTracing)
            ConsoleReporter.Debug($"      -> EMIT statement: {target.Emit()} = {rhs.Emit()} (decl={isFirstDef}, effectiveUses={effectiveUseCount})");
    }

    private bool HasEmittableSideEffects(IrInstruction inst, ExprNode rhs)
    {
        bool hasSideEffects = inst.Opcode is IrOpcode.Call or IrOpcode.IndirectCall or IrOpcode.TailCall;
        if (hasSideEffects && rhs is not ExprCall and not ExprNew)
            hasSideEffects = false;
        if (rhs is ExprAssign { Value: ExprNew })
            hasSideEffects = true;
        return hasSideEffects;
    }

    private int GetEffectiveUseCount(SsaVariable destSsa, int useCount)
    {
        int effectiveUseCount = useCount;
        if (useCount <= 1)
            return effectiveUseCount;

        foreach (var (useBlockId, useInstrIdx) in _defUse.GetUses(destSsa))
        {
            if (useInstrIdx < 0) continue;

            var useBlock = _cfg.FindBlock(useBlockId);
            if (useBlock == null || useInstrIdx >= useBlock.Instructions.Count)
                continue;

            var useInst = useBlock.Instructions[useInstrIdx];
            var useDest = _ssa.GetDestination(useInst.Address);
            if (useDest.HasValue && _defUse.UseCount(useDest.Value) == 0)
                effectiveUseCount--;
        }

        return effectiveUseCount < 0 ? 0 : effectiveUseCount;
    }

    private void ProcessVoidExpression(IrInstruction inst, ExprNode rhs, List<ExprStatement> stmts)
    {
        if (ShouldSkipStackConstructorTemp(rhs))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug("      -> SKIP ctor temp (stack slot consumed by field store)");
            return;
        }

        stmts.Add(new ExprStatement { Expr = rhs, Inst = inst });
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      -> EMIT void statement: {rhs.Emit()}");
    }

    private bool ShouldSkipStackConstructorTemp(ExprNode rhs)
    {
        if (rhs is not ExprAssign { Target: ExprSpSlot spSlot })
            return false;

        return _ctx.StackSlotValues.ContainsKey(spSlot.Offset);
    }

    private void ProcessNoOpInstruction(IrInstruction inst, SsaVariable? destSsa)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      -> NO-OP (rhs=false, dest={destSsa.HasValue})");

        if (!destSsa.HasValue)
            return;

        var prevExpr = GetRegExpr(destSsa.Value.VarId, inst.Address, -1);
        ExprMap[destSsa.Value] = prevExpr;
        Inlined.Add(destSsa.Value);
        if (ConsoleReporter.IsTracing)
            ConsoleReporter.Debug($"        -> NO-OP var propagation: mapped {destSsa.Value.Name} v{destSsa.Value.Version} to {prevExpr.Emit()}");
    }

    private ExprNode? BuildExpression(IrInstruction inst)
    {
        var result = inst.Opcode switch
        {
            IrOpcode.Call or IrOpcode.IndirectCall or IrOpcode.TailCall => BuildCall(inst),
            IrOpcode.Load => BuildLoad(inst),
            IrOpcode.LoadImmediate => BuildLoadImm(inst),
            IrOpcode.LoadAddress => BuildLoadAddr(inst),
            IrOpcode.Assign => BuildAssign(inst),
            IrOpcode.Add or IrOpcode.FAdd => BuildBinary(inst, "+"),
            IrOpcode.Sub or IrOpcode.FSub => BuildBinary(inst, "-"),
            IrOpcode.Mul or IrOpcode.FMul => BuildBinary(inst, "*"),
            IrOpcode.SDiv or IrOpcode.UDiv or IrOpcode.FDiv => BuildBinary(inst, "/"),
            IrOpcode.And => BuildBinary(inst, "&"),
            IrOpcode.Or => BuildBinary(inst, "|"),
            IrOpcode.Xor => BuildBinary(inst, "^"),
            IrOpcode.Shl => BuildBinary(inst, "<<"),
            IrOpcode.Shr or IrOpcode.Sar => BuildBinary(inst, ">>"),
            IrOpcode.Neg or IrOpcode.FNeg => BuildUnary(inst, "-"),
            IrOpcode.Not => BuildUnary(inst, "~"),
            IrOpcode.Compare or IrOpcode.FCompare => BuildCompare(inst),
            IrOpcode.Select or IrOpcode.SelectInc or IrOpcode.SelectInv or IrOpcode.SelectNeg => BuildCondSelect(inst),
            IrOpcode.SignExtend8 or IrOpcode.SignExtend16 or IrOpcode.SignExtend32 => BuildExtend(inst),
            IrOpcode.ZeroExtend8 or IrOpcode.ZeroExtend16 => BuildExtend(inst),
            IrOpcode.FloatToSignedInt or IrOpcode.FloatToUnsignedInt or IrOpcode.SignedIntToFloat or IrOpcode.UnsignedIntToFloat => BuildCast(inst, "float"),
            IrOpcode.FloatExtend => BuildCast(inst, "double"),
            IrOpcode.FloatTruncate => BuildCast(inst, "float"),
            IrOpcode.Bitcast => BuildCast(inst, "bitcast"),
            IrOpcode.FSqrt => BuildMathFunc(inst, "Mathf.Sqrt"),
            IrOpcode.FAbs => BuildMathFunc(inst, "Mathf.Abs"),
            IrOpcode.FRound => BuildMathFunc(inst, "Mathf.Round"),
            IrOpcode.FMin => BuildMathFuncBinary(inst, "Mathf.Min"),
            IrOpcode.FMax => BuildMathFuncBinary(inst, "Mathf.Max"),
            IrOpcode.FNMul => BuildBinary(inst, "*"),
            IrOpcode.FMulAdd => BuildFusedMulAdd(inst, "+"),
            IrOpcode.FMulSub => BuildFusedMulAdd(inst, "-"),
            IrOpcode.BitfieldExtractUnsigned or IrOpcode.BitfieldExtractSigned or IrOpcode.BitfieldInsert => BuildBitfield(inst),
            _ => null
        };
        if (ConsoleReporter.IsTracing)
            ConsoleReporter.Debug($"      BuildExpression({inst.Opcode}) -> {result?.GetType().Name ?? "null"}({result?.Emit() ?? ""})");
        return result;
    }
}