using System;
using System.Collections.Generic;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Analysis.Utils;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

/// <summary>Store expression builder: merged store splitting, Q-register SIMD, type-hint decoding.</summary>
public sealed partial class ExprPropagator
{
    private ExprNode? BuildStore(IrInstruction inst)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      BuildStore: sources={inst.Sources.Length} ann=\"{inst.Annotation ?? "null"}\"");

        if (inst.Sources.Length < 2)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → null (< 2 sources)");
            return null;
        }
        var dst = inst.Sources[0];
        var val = inst.Sources[1];

        ExprNode target = BuildStoreTarget(inst, dst, val);

        ExprNode value = GetSourceExpr(inst, 1);
        if (value == null)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → null (value is null)");
            return null;
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildStore -> value evaluated to type: {value.GetType().Name}, emit: {value.Emit()}");
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        value: {value.Emit()} (bits={val.BitWidth})");

        // 1. Try handle inlined array init and memcpy
        if (TryHandleInlinedArrayInitAndMemcpy(target, value))
            return null;

        // 2. Try handle 128-bit Q-register store (multi-field splitting / struct decomposition)
        if (TryHandleQRegisterStore(inst, dst, val, target, value, out var qRegRes))
            return qRegRes;

        // 3. Try handle 64-bit merged store detection
        if (TryHandleMerged64Store(inst, dst, val, target, value, out var merged64Res))
            return merged64Res;

        // 4. Try handle 128-bit Q-register SIMD store (packed4)
        if (TryHandleQRegisterSimdPackedStore(inst, dst, val, value, out var simdPackedRes))
            return simdPackedRes;

        // 5. Try handle unannotated 64-bit SP store (split to structs)
        if (TryHandleUnannotated64SpStore(inst, dst, val, value, out var spStoreRes))
            return spStoreRes;

        // Track stack slot values
        if (dst.Kind == IrOperandKind.Memory && Rosetta.Analysis.Utils.ArmUtils.IsStackPointer(dst.Value))
        {
            bool isCalleeSavedSpill = value is ExprVar ev && ev.Version == 0 && Rosetta.Analysis.Utils.ExprUtils.IsCalleeSavedRegister(ev.VarId);
            if (isCalleeSavedSpill)
            {
                _ctx.CalleeSavedSpillSlots.Add(dst.Offset);
            }
            else if (!_ssa.AliasedStackSlots.Contains(200 + (int)dst.Offset))
            {
                _ctx.StackSlotValues[dst.Offset] = value;
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → tracked stack slot [SP+0x{dst.Offset:X}] = {value.Emit()}");
            }
        }
        else if (dst.Kind == IrOperandKind.Memory && dst.Value == 29 && _ctx.FpSpOffset >= 0)
        {
            long effectiveSpOffset = _ctx.FpSpOffset + dst.Offset;
            bool isCalleeSavedSpill = value is ExprVar ev && ev.Version == 0 && Rosetta.Analysis.Utils.ExprUtils.IsCalleeSavedRegister(ev.VarId);
            if (isCalleeSavedSpill)
            {
                _ctx.CalleeSavedSpillSlots.Add(effectiveSpOffset);
            }
            else if (!_ssa.AliasedStackSlots.Contains(200 + (int)effectiveSpOffset))
            {
                _ctx.StackSlotValues[effectiveSpOffset] = value;
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → tracked FP stack slot [SP+0x{effectiveSpOffset:X}] = {value.Emit()} (x29 + {dst.Offset})");
            }
        }

        // 6. Coerce store value (type-hint decoding, bool coercion for array elements)
        value = CoerceStoreValue(target, value, inst.Annotation);

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → {target.Emit()} = {value.Emit()}");
        return new ExprAssign(target, value);
    }
}