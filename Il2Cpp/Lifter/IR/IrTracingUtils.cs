using System;
using System.Collections.Generic;
using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Lifter.IR;

public static class IrTracingUtils
{
    public static (IrInstruction? inst, int index) FindDefinitionSatisfying( List<IrInstruction> insts, int startIdx, long regNum, Predicate<IrInstruction> predicate, int searchLimit = 32, IrOperandKind regKind = IrOperandKind.Register)
    {
        int minIdx = Math.Max(0, startIdx - searchLimit);
        for (int j = startIdx - 1; j >= minIdx; j--)
        {
            var prev = insts[j];
            if (prev.Destination.HasValue &&
                prev.Destination.Value.Kind == regKind &&
                prev.Destination.Value.Value == regNum &&
                predicate(prev))
            {
                return (prev, j);
            }
        }
        return (null, -1);
    }

    public static (IrInstruction? inst, int index) FindDefinition(List<IrInstruction> insts, int startIdx, long regNum, int searchLimit = 32, IrOperandKind regKind = IrOperandKind.Register)
    {
        return FindDefinitionSatisfying(insts, startIdx, regNum, _ => true, searchLimit, regKind);
    }
}
