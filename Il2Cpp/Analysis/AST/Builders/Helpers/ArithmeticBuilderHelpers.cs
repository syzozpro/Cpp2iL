using System;
using Rosetta.Analysis.IR;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

public sealed partial class ExprPropagator
{
    /// <summary>Find the preceding Test/Compare and build the condition expression.</summary>
    private ExprNode? BuildConditionFromPrecedingFlags(IrInstruction selectInst, string condName)
    {
        var block = _cfg.FindBlockByAddress(selectInst.Address);
        if (block == null) return null;

        // Walk backward to find the Test or Compare instruction
        IrInstruction? flagSetter = null;
        for (int i = block.Instructions.Count - 1; i >= 0; i--)
        {
            var bi = block.Instructions[i];
            if (bi.Address >= selectInst.Address) continue;
            if (bi.Opcode is IrOpcode.Test or IrOpcode.Compare or IrOpcode.FCompare)
            {
                flagSetter = bi;
                break;
            }
        }

        if (flagSetter == null) return null;

        var left = GetSourceExpr(flagSetter, 0);
        var right = flagSetter.Sources.Length > 1 ? GetSourceExpr(flagSetter, 1) : new ExprLiteral(0);

        if (flagSetter.Opcode == IrOpcode.Test)
        {
            // TST sets Z flag if (reg & mask) == 0
            // eq → (reg & mask) == 0, ne → (reg & mask) != 0
            string op = condName == "eq" ? "==" : "!=";
            var bitAnd = BuildTypedBinary("&", left, right);
            return BuildTypedBinary(op, bitAnd, new ExprLiteral(0));
        }

        // CMP: left op right
        string cmpOp = Rosetta.Analysis.Utils.OpUtils.ConditionToOperator(condName);
        return BuildTypedBinary(cmpOp, left, right);
    }

    /// <summary>Try to extract a long value from an ExprLiteral (handles int, long, uint, string hex).</summary>
    private static bool TryGetLongValue(ExprNode expr, out long value)
    {
        value = 0;
        if (expr is not ExprLiteral lit) return false;
        switch (lit.Value)
        {
            case int i: value = i; return true;
            case long l: value = l; return true;
            case uint u: value = u; return true;
            case ulong ul when ul <= (ulong)long.MaxValue: value = (long)ul; return true;
            case string s when s.StartsWith("0x") && long.TryParse(s[2..],
                System.Globalization.NumberStyles.HexNumber, null, out long parsed):
                value = parsed;
                return true;
            default: return false;
        }
    }

    private ExprNode? TryFoldArm64ZeroExtensionMasks(ExprNode left, ExprNode right, string op)
    {
        if (op != "&") return null;
        if (!TryGetLongValue(right, out long maskVal)) return null;

        // x & 0xFFFFFFFF → x (32-bit zero extension = identity for W values)
        if (maskVal == 0xFFFFFFFFL || maskVal == unchecked((long)0xFFFFFFFFL))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildBinary(&) fold W→X zero-ext: {left.Emit()} & 0xFFFFFFFF → {left.Emit()}");
            return left;
        }
        // x & 0xFFFFFFFE → x (even mask = bounds check artifact, strip bit 0)
        if (maskVal == 0xFFFFFFFEL || maskVal == unchecked((long)0xFFFFFFFEL))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildBinary(&) fold even mask: {left.Emit()} & 0xFFFFFFFE → {left.Emit()}");
            return left;
        }
        // x & 0xFFFFFFFC → x (quad mask = bounds check artifact, strip bits 0-1)
        if (maskVal == 0xFFFFFFFCL || maskVal == unchecked((long)0xFFFFFFFCL))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildBinary(&) fold quad mask: {left.Emit()} & 0xFFFFFFFC → {left.Emit()}");
            return left;
        }

        return null;
    }

    private ExprNode? TryFoldArm64SignExtensionMasks(ExprNode left, ExprNode right, string op)
    {
        if (op != "+" && op != "&") return null;

        if (TryGetLongValue(right, out long addMask) && addMask == unchecked((long)0xFFFFFFFF00000000L))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildBinary({op}) strip upper-32 mask: {left.Emit()} {op} 0xFFFFFFFF00000000 → {left.Emit()}");
            return left;
        }
        // Also handle when the mask is on the left side
        if (TryGetLongValue(left, out long leftMask) && leftMask == unchecked((long)0xFFFFFFFF00000000L))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      BuildBinary({op}) strip upper-32 mask (left): 0xFFFFFFFF00000000 {op} {right.Emit()} → {right.Emit()}");
            return right;
        }

        return null;
    }
}
