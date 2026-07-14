using System.Collections.Generic;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Common;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;
using ArmUtils = Rosetta.Analysis.Utils.ArmUtils;


namespace Rosetta.Analysis.AST;

/// <summary>SSA variable resolution: Resolve, GetSourceExpr, GetRegExpr, MakeVarExpr.</summary>
public sealed partial class ExprPropagator
{
    /// <summary>Resolve an SSA variable to its expression (inline if single-use).</summary>
    public ExprNode Resolve(SsaVariable v)
    {
        if (ExprMap.TryGetValue(v, out var expr))
        {
            if (Inlined.Contains(v))
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        Resolve({v.Name} v{v.Version}) → inlined: {expr.Emit()}");
                return expr;
            }
            var varExpr = MakeVarExpr(v);
            varExpr.InferredType ??= expr.InferredType;
            varExpr.MetadataTypeDefIndex = expr.MetadataTypeDefIndex;
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        Resolve({v.Name} v{v.Version}) → var: {varExpr.Emit()}");
            return varExpr;
        }

        if (!_method.IsStatic && v.VarId == 0 && v.Version == 0)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        Resolve({v.Name} v{v.Version}) → this");
            return new ExprThis();
        }

        var fallback = MakeVarExpr(v);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        Resolve({v.Name} v{v.Version}) → fallback var: {fallback.Emit()}");
        return fallback;
    }

    private ExprNode GetSourceExpr(IrInstruction inst, int srcIdx)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] GetSourceExpr START! inst.Opcode={inst.Opcode}, srcIdx={srcIdx}");
        if (srcIdx >= inst.Sources.Length)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        GetSourceExpr(idx={srcIdx}) → ? (out of bounds, len={inst.Sources.Length})");
            return new ExprLiteral("?");
        }
        var src = inst.Sources[srcIdx];
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] GetSourceExpr -> src.Kind={src.Kind}, src.Value=0x{src.Value:X}");

        var ssaVar = _ssa.GetSource(inst.Address, srcIdx);
        if (ssaVar.HasValue)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] GetSourceExpr -> SSA found! Var={ssaVar.Value.Name} v{ssaVar.Value.Version}");
            var resolved = Resolve(ssaVar.Value);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] GetSourceExpr -> SSA resolved to type: {resolved.GetType().Name}, emit: {resolved.Emit()}");
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        GetSourceExpr(idx={srcIdx}) → SSA {ssaVar.Value.Name} v{ssaVar.Value.Version} = {resolved.Emit()}");
            return resolved;
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] GetSourceExpr -> No SSA, falling back to switch on src.Kind");
        ExprNode result = src.Kind switch
        {
            IrOperandKind.Register => GetRegExpr(src.Value, inst.Address, srcIdx),
            IrOperandKind.Immediate => new ExprLiteral(FormatImm(src.Value, src.BitWidth)),
            IrOperandKind.FloatImmediate => new ExprLiteral(FormatFloat(src.Value, src.BitWidth)),
            IrOperandKind.SimdImmediate => new ExprSimd(src.Value, src.Offset),
            IrOperandKind.Memory when ArmUtils.IsStackPointer(src.Value) =>
                ResolveStackSlot(src.Offset),
            IrOperandKind.Memory => BuildMemoryExpr(inst, srcIdx, src),
            IrOperandKind.Label => new ExprLiteral($"0x{(ulong)src.Value:X}"),
            _ => new ExprLiteral("?")
        };
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] GetSourceExpr -> Fallback created type: {result.GetType().Name}, emit: {result.Emit()}");
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        GetSourceExpr(idx={srcIdx}) → fallback {src.Kind}: {result.Emit()}");
        return result;
    }

    /// <summary>Build expression for non-SP Memory operand [base + offset].</summary>
    private ExprNode BuildMemoryExpr(IrInstruction inst, int srcIdx, IrOperand src)
    {
        ExprNode baseExpr;

        if (_ssa.MemoryBaseMap.TryGetValue((inst.Address, srcIdx), out var memBaseSsa))
        {
            baseExpr = Resolve(memBaseSsa);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        BuildMemoryExpr: SSA memory base {memBaseSsa.Name} v{memBaseSsa.Version} → {baseExpr.Emit()}");
        }
        else
        {
            baseExpr = GetRegExpr(src.Value, inst.Address, srcIdx);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        BuildMemoryExpr: fallback reg{src.Value} → {baseExpr.Emit()}");
        }

        var result = TryMakeArrayAccess(baseExpr, src.Offset) ?? new ExprMemory(baseExpr, src.Offset);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        BuildMemoryExpr: offset=0x{src.Offset:X} → {result.Emit()}");
        return result;
    }

    private ExprNode GetRegExpr(long regNum, ulong address, int srcIdx)
    {
        if (ArmUtils.IsStackPointer(regNum)) return new ExprVar("SP");

        int varId = (int)regNum;
        var useBlock = _cfg.FindBlockByAddress(address);
        int useBlockId = useBlock?.Id ?? -1;

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        GetRegExpr: reg={regNum} addr=0x{address:X} useBlock={useBlockId}");

        if (useBlockId >= 0 && useBlockId < _ssa.DomTree.BlockCount)
        {
            var defsPerBlock = new Dictionary<int, List<(SsaVariable var, ulong addr)>>();
            if (_ctx.VarDefsMap.TryGetValue(varId, out var defs))
            {
                foreach (var (v, bId, dAddr) in defs)
                {
                    if (!defsPerBlock.TryGetValue(bId, out var list))
                    {
                        list = new();
                        defsPerBlock[bId] = list;
                    }
                    list.Add((v, dAddr));
                }
            }

            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          defs found: {defsPerBlock.Values.Sum(l => l.Count)} across {defsPerBlock.Count} blocks");

            // Same-block def before use
            if (defsPerBlock.TryGetValue(useBlockId, out var sameBlockDefs))
            {
                SsaVariable? bestInBlock = null;
                ulong bestAddr = 0;
                foreach (var (v, dAddr) in sameBlockDefs)
                {
                    if (dAddr < address && (bestInBlock == null || dAddr > bestAddr))
                    {
                        bestInBlock = v;
                        bestAddr = dAddr;
                    }
                }
                if (bestInBlock.HasValue)
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          same-block def: {bestInBlock.Value.Name} v{bestInBlock.Value.Version} at 0x{bestAddr:X}");
                    return Resolve(bestInBlock.Value);
                }
            }

            // IDom chain walk
            int walker = _ssa.DomTree.IDom[useBlockId];
            var visited = new HashSet<int> { useBlockId };
            while (walker >= 0 && visited.Add(walker))
            {
                if (defsPerBlock.TryGetValue(walker, out var blockDefs))
                {
                    SsaVariable? best = null;
                    ulong bestAddr = 0;
                    foreach (var (v, dAddr) in blockDefs)
                    {
                        if (best == null || dAddr > bestAddr)
                        {
                            best = v;
                            bestAddr = dAddr;
                        }
                    }
                    if (best.HasValue)
                    {
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          IDom def: block={walker} {best.Value.Name} v{best.Value.Version} at 0x{bestAddr:X}");
                        return Resolve(best.Value);
                    }
                }

                if (walker == _ssa.DomTree.IDom[walker])
                    break;
                walker = _ssa.DomTree.IDom[walker];
            }
        }

        // Parameter fallback
        if (_method.GpParamMap.TryGetValue((int)regNum, out var paramName))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → parameter: {paramName} (reg={regNum})");
            return new ExprVar(paramName);
        }

        // Raw register fallback
        string prefix = regNum >= 32 ? "s" : "x";
        long displayNum = regNum >= 32 ? regNum - 32 : regNum;
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → raw register: {prefix}{displayNum}");
        return new ExprVar($"{prefix}{displayNum}");
    }

    /// <summary>Get the address of a definition site, or 0 if not found.</summary>
    private ulong GetDefAddress((int blockId, int instrIndex) defSite)
    {
        var block = _cfg.FindBlock(defSite.blockId);
        if (block != null && defSite.instrIndex >= 0 && defSite.instrIndex < block.Instructions.Count)
            return block.Instructions[defSite.instrIndex].Address;
        return 0;
    }

    private ExprNode MakeVarExpr(SsaVariable v)
    {
        if (v.Version == 0 && _method.Parameters.Count > 0)
        {
            if (!_method.IsStatic && v.VarId == 0)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        MakeVarExpr({v.Name} v0) → this");
                return new ExprThis();
            }

            string? paramName = v.IsFloat
                ? (_method.FpParamMap.TryGetValue(v.RegisterNumber, out var fpName) ? fpName : null)
                : (_method.GpParamMap.TryGetValue(v.RegisterNumber, out var gpName) ? gpName : null);

            if (paramName != null)
            {
                if (ConsoleReporter.IsTracing)
                {
                    string pKind = v.IsFloat ? "FP" : "GP";
                    ConsoleReporter.Debug($"        MakeVarExpr({v.Name} v0) → {pKind} param: {paramName} (reg={v.RegisterNumber})");
                }
                var expr = new ExprVar(paramName, v.VarId, v.Version);
                ApplyParameterTypeMetadata(expr, v.RegisterNumber, v.IsFloat);
                return expr;
            }
        }

        // Stack slot variables (VarId >= 200): instantiate natively as ExprSpSlot
        if (v.IsStackSlot)
        {
            int spOffset = v.VarId - 200;
            var resolved = ResolveStackSlot(spOffset);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        MakeVarExpr({v.Name}) → stack slot: {resolved.Emit()}");
            return resolved;
        }

        byte bitWidth = _ctx.VarBitWidths.TryGetValue((v.VarId, v.Version), out var bw) ? bw : v.BitWidth;

        string prefix;
        if (v.IsFloat)
        {
            if (v.ElementCount > 1)
            {
                prefix = $"v{v.RegisterNumber}";
            }
            else
            {
                prefix = $"{bitWidth switch { 32 => "s", 64 => "d", 128 => "q", _ => "v" }}{v.RegisterNumber}";
            }
        }
        else
        {
            if (_method.IsArm32)
            {
                prefix = v.RegisterNumber == 13 ? "SP" : v.RegisterNumber == 15 ? "PC" : v.RegisterNumber == 14 ? "LR" : $"R{v.RegisterNumber}";
            }
            else
            {
                prefix = $"{(bitWidth > 32 ? "x" : "w")}{v.RegisterNumber}";
            }
        }

        string name = _ctx.MultiVersionRegs.Contains(v.VarId)
            ? $"{prefix}_{v.Version}"
            : prefix;

        return new ExprVar(name, v.VarId, v.Version, v.ElementWidth, v.ElementCount);
    }

    private void ApplyParameterTypeMetadata(ExprNode expr, int registerNumber, bool isFloat)
    {
        if (_typeModel == null ||
            !_typeModel.Signatures.TryGetValue(_method.MethodIndex, out var sig))
            return;

        int paramIndex = GetParameterIndexForRegister(registerNumber, isFloat, sig.IsStatic);
        if (paramIndex < 0 || paramIndex >= sig.Parameters.Count)
            return;

        int typeIndex = sig.Parameters[paramIndex].TypeIndex;
        if (typeIndex >= 0)
            expr.MetadataTypeDefIndex = _typeModel.ResolveTypeDefIndexFromTypeIndex(typeIndex);
    }

    private static int GetParameterIndexForRegister(int registerNumber, bool isFloat, bool isStatic)
    {
        if (isFloat)
            return registerNumber;

        int firstGpParamRegister = isStatic ? 0 : 1;
        return registerNumber - firstGpParamRegister;
    }

    private ExprNode ResolveStackSlot(long spOffset)
    {
        if (_ctx.StackSlotValues.TryGetValue(spOffset, out var slotVal))
            return slotVal;

        var fieldExpr = TryResolveStackStructField(spOffset);
        return fieldExpr ?? new ExprSpSlot(spOffset);
    }

    private ExprNode? TryResolveStackStructField(long spOffset)
    {
        if (_typeModel == null || _ctx.StackStructReturnTypes.Count == 0)
            return null;

        foreach (var (baseOffset, typeName) in _ctx.StackStructReturnTypes)
        {
            if (spOffset <= baseOffset)
                continue;

            if (!_ctx.StackSlotValues.TryGetValue(baseOffset, out var baseExpr))
                continue;

            long relativeOffset = spOffset - baseOffset;
            var resolved = TryResolveFieldPath(baseExpr, TypeUtils.StripModifiers(typeName), relativeOffset, 0);
            if (resolved != null)
                return resolved;
        }

        return null;
    }

    private ExprNode? TryResolveFieldPath(ExprNode baseExpr, string typeName, long relativeOffset, int depth = 0)
    {
        if (_typeModel == null || depth > 8)
            return null;

        if (!_typeModel.FieldLayoutsByTypeName.TryGetValue(typeName, out int typeDefIndex) ||
            !_typeModel.FieldLayouts.TryGetValue(typeDefIndex, out var layout))
            return null;

        foreach (var field in layout.Fields)
        {
            if (field.IsStatic || field.Offset < 0)
                continue;

            long fieldOffset = field.Offset - Constants.ObjectHeaderSize;
            if (fieldOffset < 0)
                fieldOffset = field.Offset;

            if (relativeOffset == fieldOffset)
            {
                string fieldName = Rosetta.Analysis.Utils.StringUtils.CleanFieldName(field.Name, _typeModel);
                bool isProp = false;
                if (Rosetta.Common.TypeUtils.IsStandardCollectionType(typeName) && (fieldName == "_size" || fieldName == "_count" || fieldName == "size" || fieldName == "count"))
                {
                    fieldName = "Count";
                    isProp = true;
                }
                var fieldExpr = new ExprField(baseExpr, fieldName, isProp);
                return TryResolveFieldPath(fieldExpr, TypeUtils.StripModifiers(field.TypeName), 0, depth + 1) ?? fieldExpr;
            }

            if (relativeOffset <= fieldOffset)
                continue;

            string nestedFieldName = Rosetta.Analysis.Utils.StringUtils.CleanFieldName(field.Name, _typeModel);
            bool nestedIsProp = false;
            if (Rosetta.Common.TypeUtils.IsStandardCollectionType(typeName) && (nestedFieldName == "_size" || nestedFieldName == "_count" || nestedFieldName == "size" || nestedFieldName == "count"))
            {
                nestedFieldName = "Count";
                nestedIsProp = true;
            }
            var nestedBase = new ExprField(baseExpr, nestedFieldName, nestedIsProp);
            var nested = TryResolveFieldPath(nestedBase, TypeUtils.StripModifiers(field.TypeName), relativeOffset - fieldOffset, depth + 1);
            if (nested != null)
                return nested;
        }

        return null;
    }
}
