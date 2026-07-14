using System;
using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Lifter.IR.RuntimeHelpers;

public sealed class StructFieldRefClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null && context.X0Def.Opcode == IrOpcode.Add)
        {
            if (context.X0Def.Sources.Length >= 2 &&
                context.X0Def.Sources[0].Kind == IrOperandKind.Register &&
                IrRegisterConstants.IsStackPointer(context.X0Def.Sources[0].Value)) // SP
            {
                context.X0Def.Annotation = "struct_box_ptr";
                return "struct_box_local";
            }
            return "struct_field_ref";
        }
        return null;
    }
}
