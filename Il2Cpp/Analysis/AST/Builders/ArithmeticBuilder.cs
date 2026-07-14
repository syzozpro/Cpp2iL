using Rosetta.Analysis.IR;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;
using Rosetta.Analysis.Utils;

namespace Rosetta.Analysis.AST;

/// <summary>Arithmetic/logic expression builders: binary ops, compare, extend, cast, bitfield.</summary>
public sealed partial class ExprPropagator
{
    private ExprNode BuildAssign(IrInstruction inst)
    {
        if (inst.Sources.Length == 0) return new ExprLiteral("?");
        var result = GetSourceExpr(inst, 0);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildAssign → {result.Emit()}");
        return result;
    }

    private ExprNode BuildMathFunc(IrInstruction inst, string funcName)
    {
        var operand = inst.Sources.Length > 0 ? GetSourceExpr(inst, 0) : new ExprLiteral(0);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildMathFunc({funcName}) → {funcName}({operand.Emit()})");
        return new ExprCall(funcName, null, [operand]);
    }

    private ExprNode BuildMathFuncBinary(IrInstruction inst, string funcName)
    {
        var left = inst.Sources.Length > 0 ? GetSourceExpr(inst, 0) : new ExprLiteral(0);
        var right = inst.Sources.Length > 1 ? GetSourceExpr(inst, 1) : new ExprLiteral(0);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildMathFuncBinary({funcName}) → {funcName}({left.Emit()}, {right.Emit()})");
        return new ExprCall(funcName, null, [left, right]);
    }


    private ExprNode BuildFusedMulAdd(IrInstruction inst, string addOp)
    {
        var rn = inst.Sources.Length > 0 ? GetSourceExpr(inst, 0) : new ExprLiteral(0);
        var rm = inst.Sources.Length > 1 ? GetSourceExpr(inst, 1) : new ExprLiteral(0);
        var ra = inst.Sources.Length > 2 ? GetSourceExpr(inst, 2) : new ExprLiteral(0);
        var mulExpr = new ExprBinary("*", rn, rm);
        ExprNode result = new ExprBinary(addOp, ra, mulExpr);

        if (inst.Annotation == "negated")
        {
            result = new ExprUnary("-", result);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildFusedMulAdd({addOp}) negated → {result.Emit()}");
        }
        else
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildFusedMulAdd({addOp}) → {result.Emit()}");
        }

        return result;
    }

    private ExprNode BuildBinary(IrInstruction inst, string op)
    {
        var left = inst.Sources.Length > 0 ? GetSourceExpr(inst, 0) : new ExprLiteral(0);
        var right = inst.Sources.Length > 1 ? GetSourceExpr(inst, 1) : new ExprLiteral(0);

        if (inst.Annotation != null && op == "+")
        {
            if (inst.Annotation.StartsWith("->"))
                return new ExprField(left, Rosetta.Analysis.Utils.StringUtils.CleanFieldName(inst.Annotation[2..], _typeModel));
            return new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanFieldName(inst.Annotation, _typeModel));
        }

        if (op == "+" && left is ExprVar spVar && spVar.Name == "SP" && right is ExprLiteral lit && lit.Value is int or long)
        {
            long offset = lit.Value is int i ? i : (long)lit.Value;
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildBinary(+) SP+offset → local_sp{offset:X}");
            return new ExprSpSlot(offset);
        }

        // ── OR-as-ADD for stack pointer field offsets ──
        // ARM64 optimization: compiler uses OR instead of ADD to compute struct field
        // addresses when the base is aligned. E.g., (SP+0x20) | 8 = SP+0x28 because
        // SP+0x20 is 16-byte aligned, so the lower bits are zero and OR == ADD.
        // Recognizes ExprSpSlot | small_const → ExprSpSlot(N+const)
        if (op == "|" && right is ExprLiteral orLit && TryGetLongValue(right, out long orVal) && orVal > 0)
        {
            long? baseSpOffset = ExprUtils.TryGetSpSlotOffset(left);
            if (baseSpOffset.HasValue)
            {
                long combined = baseSpOffset.Value + orVal;
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildBinary(|) SP-relative OR→ADD: local_sp{baseSpOffset.Value:X} | {orVal} → local_sp{combined:X}");
                return new ExprSpSlot(combined);
            }
        }

        // Strip IL2CPP bool masking: x & 1 → x (AND with 1 is a no-op for bools)
        if (op == "&" && right is ExprLiteral andLit && ((andLit.Value is int andI && andI == 1) || (andLit.Value is long andL && andL == 1)))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildBinary(&) strip bool mask: {left.Emit()} & 1 → {left.Emit()}");
            return left;
        }

        // ── W→X zero-extension folding ──
        var zeroExtFold = TryFoldArm64ZeroExtensionMasks(left, right, op);
        if (zeroExtFold != null) return zeroExtFold;

        // ── 0xFFFFFFFF00000000 upper-mask folding ──
        var signExtFold = TryFoldArm64SignExtensionMasks(left, right, op);
        if (signExtFold != null) return signExtFold;

        var typedBinary = BuildTypedBinary(op, left, right);
        if (typedBinary.MetadataTypeDefIndex >= 0)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildBinary({op}) enum → {typedBinary.Emit()}");
            return typedBinary;
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildBinary({op}) → {left.Emit()} {op} {right.Emit()}");
        return typedBinary;
    }

    private ExprBinary BuildTypedBinary(string op, ExprNode left, ExprNode right)
    {
        if (_typeModel == null || op is not ("|" or "&" or "^" or "==" or "!="))
            return new ExprBinary(op, left, right);

        int typeDefIndex = left.MetadataTypeDefIndex >= 0
            ? left.MetadataTypeDefIndex
            : right.MetadataTypeDefIndex;
        if (typeDefIndex < 0)
            return new ExprBinary(op, left, right);

        left = CoerceEnumLiteral(left, typeDefIndex);
        right = CoerceEnumLiteral(right, typeDefIndex);

        var result = new ExprBinary(op, left, right);
        result.MetadataTypeDefIndex = typeDefIndex;
        return result;
    }

    private ExprNode CoerceEnumLiteral(ExprNode expr, int typeDefIndex)
    {
        if (!TryGetLongValue(expr, out long value))
            return expr;

        string? literal = _typeModel?.ResolveEnumLiteralByTypeDefIndex(typeDefIndex, value);
        return literal != null ? new ExprVar(literal) { MetadataTypeDefIndex = typeDefIndex } : expr;
    }

    private ExprNode BuildUnary(IrInstruction inst, string op)
    {
        var operand = inst.Sources.Length > 0 ? GetSourceExpr(inst, 0) : new ExprLiteral(0);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildUnary({op}) → {op}{operand.Emit()}");
        return new ExprUnary(op, operand);
    }

    private ExprNode BuildCompare(IrInstruction inst)
    {
        int leftIdx = 0;
        int rightIdx = 1;
        if (inst.Sources.Length >= 3 && inst.Sources[0].Kind == IrOperandKind.Condition)
        {
            leftIdx = 1;
            rightIdx = 2;
        }
        var left = inst.Sources.Length > leftIdx ? GetSourceExpr(inst, leftIdx) : new ExprLiteral(0);
        var right = inst.Sources.Length > rightIdx ? GetSourceExpr(inst, rightIdx) : new ExprLiteral(0);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCompare → {left.Emit()} == {right.Emit()}");
        return BuildTypedBinary("==", left, right);
    }

    private ExprNode BuildCondSelect(IrInstruction inst)
    {
        if (inst.Sources.Length >= 3)
        {
            var trueVal = GetSourceExpr(inst, 1);
            var falseVal = GetSourceExpr(inst, 2);
            byte condCode = inst.Sources[0].Kind == IrOperandKind.Condition
                ? (byte)inst.Sources[0].Value : (byte)0;
            string condName = condCode switch
            {
                0x0 => "eq", 0x1 => "ne", 0x2 => "hs", 0x3 => "lo",
                0xA => "ge", 0xB => "lt", 0xC => "gt", 0xD => "le",
                _ => "ne"
            };

            // ── CSET / CSETM: emit boolean condition directly ──
            // CSET Rd, cond = "Rd = (cond ? 1 : 0)".
            // The IrLifter already inverted the hardware condition so the stored
            // condition is the "positive" sense (CSET NE stores NE, meaning 1 when NE).
            // Instead of emitting a meaningless ternary (cond ? 0 : 0), emit the
            // condition expression as a boolean value.
            if (inst.Annotation is "CSET" or "CSETM")
            {
                var condExpr = BuildConditionFromPrecedingFlags(inst, condName);
                if (condExpr != null)
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCondSelect(CSET) → {condExpr.Emit()}");
                    return condExpr;
                }
            }

            // Apply specific ARM conditional select operations to the false branch
            if (inst.Opcode == IrOpcode.SelectInc)
            {
                falseVal = new ExprBinary("+", falseVal, new ExprLiteral(1));
            }
            else if (inst.Opcode == IrOpcode.SelectInv)
            {
                falseVal = new ExprUnary("~", falseVal);
            }
            else if (inst.Opcode == IrOpcode.SelectNeg)
            {
                falseVal = new ExprUnary("-", falseVal);
            }

            // Find the preceding Test/Compare in the same block to build the condition
            var condExprGeneral = BuildConditionFromPrecedingFlags(inst, condName);

            if (condExprGeneral != null)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCondSelect → {condExprGeneral.Emit()} ? {trueVal.Emit()} : {falseVal.Emit()}");
                return new ExprTernary(condExprGeneral, trueVal, falseVal);
            }

            // Fallback: no condition found, use simple ternary with literal condition
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCondSelect(fallback) → {trueVal.Emit()} ?: {falseVal.Emit()}");
            return new ExprBinary("?:", trueVal, falseVal);
        }

        var src1 = inst.Sources.Length > 0 ? GetSourceExpr(inst, 0) : new ExprLiteral("?");
        var src2 = inst.Sources.Length > 1 ? GetSourceExpr(inst, 1) : new ExprLiteral("?");
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCondSelect(fallback2) → {src1.Emit()} ?: {src2.Emit()}");
        return new ExprBinary("?:", src1, src2);
    }



    private ExprNode BuildExtend(IrInstruction inst)
    {
        var result = inst.Sources.Length > 0 ? GetSourceExpr(inst, 0) : new ExprLiteral(0);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildExtend → {result.Emit()}");
        return result;
    }

    private ExprNode BuildBitfield(IrInstruction inst)
    {
        var src = inst.Sources.Length > 0 ? GetSourceExpr(inst, 0) : new ExprLiteral(0);

        if (inst.Sources.Length >= 3 &&
            inst.Sources[1].Kind == IrOperandKind.Immediate &&
            inst.Sources[2].Kind == IrOperandKind.Immediate)
        {
            long immr = inst.Sources[1].Value;
            long imms = inst.Sources[2].Value;
            int regWidth = inst.Destination?.BitWidth == 64 ? 64 : 32;
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      BuildBitfield: immr={immr} imms={imms} regWidth={regWidth}");

            if (imms != regWidth - 1 && (imms + 1) % regWidth == immr)
            {
                int shiftAmount = regWidth - (int)immr;
                ExprNode result;
                if (shiftAmount == 1)
                    result = new ExprBinary("*", src, new ExprLiteral(2));
                else
                    result = new ExprBinary("<<", src, new ExprLiteral(shiftAmount));
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → LSL alias: {result.Emit()}");
                return result;
            }

            if (immr > imms)
            {
                int lsb = regWidth - (int)immr;
                ExprNode result;
                if (lsb > 0 && (lsb & (lsb - 1)) == 0)
                    result = new ExprBinary("*", src, new ExprLiteral(1 << lsb));
                else
                    result = new ExprBinary("<<", src, new ExprLiteral(lsb));
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → UBFIZ alias: {result.Emit()}");
                return result;
            }

            if (immr == 0 && imms == regWidth - 1)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → no-op (full register)");
                return src;
            }

            if (immr == 0)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → mask (lower bits)");
                return src;
            }

            var shiftResult = new ExprBinary(">>", src, new ExprLiteral((int)immr));
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → shift right: {shiftResult.Emit()}");
            return shiftResult;
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildBitfield fallback → {src.Emit()}");
        return src;
    }

    private ExprNode BuildCast(IrInstruction inst, string type)
    {
        string castType = inst.Opcode switch
        {
            IrOpcode.FloatToSignedInt => "int",
            IrOpcode.FloatToUnsignedInt => "uint",
            _ => type
        };

        var operand = inst.Sources.Length > 0 ? GetSourceExpr(inst, 0) : new ExprLiteral(0);

        // ── Bitcast: integer ↔ float bit-pattern reinterpretation ──
        // ARM64 pattern: w8 = LoadImmediate(Nf) → s6 = bitcast w8
        // The LoadImmediate already carries the float annotation, so the operand
        // is already an ExprLiteral with the float value. Just return it directly.
        if (castType == "bitcast")
        {
            if (IsFloatLiteral(operand))
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCast(bitcast) → folded float literal: {operand.Emit()}");
                return operand;
            }

            // SSA lookup: the source variable might resolve to a float literal
            if (operand is ExprVar)
            {
                var ssaVar = inst.Sources.Length > 0 ? _ssa.GetSource(inst.Address, 0) : null;
                if (ssaVar.HasValue && ExprMap.TryGetValue(ssaVar.Value, out var underlying) &&
                    IsFloatLiteral(underlying))
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCast(bitcast) → SSA folded float literal: {underlying.Emit()}");
                    return underlying;
                }
            }

            // If the operand is an integer literal, reinterpret its bits as float
            if (operand is ExprLiteral intLit && intLit.Value is int intVal)
            {
                float floatVal = BitConverter.Int32BitsToSingle(intVal);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCast(bitcast) → int→float reinterpret: 0x{intVal:X8} → {floatVal}f");
                return new ExprLiteral(floatVal);
            }
            if (operand is ExprLiteral longLit && longLit.Value is long longVal)
            {
                double doubleVal = BitConverter.Int64BitsToDouble(longVal);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCast(bitcast) → long→double reinterpret: 0x{longVal:X16} → {doubleVal}");
                return new ExprLiteral(doubleVal);
            }

            // Non-literal operand: the value is already semantically correct (float data
            // moving through integer register). Strip the bitcast — it's just an ARM64 artifact.
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCast(bitcast) → strip: {operand.Emit()}");
            return operand;
        }

        if (castType == "float" && IsFloatLiteral(operand))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCast({castType}) → redundant, returning {operand.Emit()}");
            return operand;
        }

        if (castType == "float" && operand is ExprVar v)
        {
            var ssaVar = inst.Sources.Length > 0 ? _ssa.GetSource(inst.Address, 0) : null;
            if (ssaVar.HasValue && ExprMap.TryGetValue(ssaVar.Value, out var underlying) &&
                IsFloatLiteral(underlying))
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCast({castType}) → SSA float literal: {underlying.Emit()}");
                return underlying;
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildCast({castType}) → ({castType}){operand.Emit()}");
        return new ExprCast(castType, operand);
    }



    private ExprNode BuildStackPointerArithmetic(ExprBinary bin)
    {
        if (bin.Left is ExprSpSlot lspVar && bin.Right is ExprLiteral litRhs && litRhs.Value is int constAdd)
        {
            long combined = lspVar.Offset + constAdd;
            return new ExprSpSlot(combined);
        }
        return bin;
    }
}
