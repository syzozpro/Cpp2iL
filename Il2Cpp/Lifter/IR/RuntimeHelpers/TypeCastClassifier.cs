using System;
using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Lifter.IR.RuntimeHelpers;

public sealed class TypeCastClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null && context.X0Def.Opcode == IrOpcode.Load &&
            context.X0Def.Sources.Length > 0 && context.X0Def.Sources[0].Kind == IrOperandKind.Memory &&
            context.X0Def.Annotation != null &&
            (context.X0Def.Annotation.Contains("typeof(") || context.X0Def.Annotation.Contains("type(")))
        {
            string typeName = IrDataResolver.ExtractTypeName(context.X0Def.Annotation);
            if (context.X1Def != null && context.X1Def.Opcode == IrOpcode.Load &&
                context.X1Def.Sources.Length > 0 && context.X1Def.Sources[0].Kind == IrOperandKind.Memory &&
                context.X1Def.Sources[0].Offset == 0)
            {
                return $"cast<{typeName}>";
            }
            return $"is<{typeName}>";
        }
        return null;
    }
}
