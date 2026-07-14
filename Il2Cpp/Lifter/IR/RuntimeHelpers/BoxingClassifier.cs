using System;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR.RuntimeHelpers;

public sealed class BoxingClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null && context.X1Def != null &&
            context.X1Def.Opcode == IrOpcode.Add &&
            context.X1Def.Sources.Length >= 2 &&
            context.X1Def.Sources[0].Kind == IrOperandKind.Register &&
            IrRegisterConstants.IsStackPointer(context.X1Def.Sources[0].Value)) // x1/R1 = SP + N
        {
            context.X1Def.Annotation = "struct_box_ptr";
            
            // Variant A: x0 = load [type_array + offset]
            if (context.X0Def.Opcode == IrOpcode.Load && context.X0Def.Sources.Length > 0 &&
                context.X0Def.Sources[0].Kind == IrOperandKind.Memory)
            {
                long x0Offset = context.X0Def.Sources[0].Offset;
                string typeName = context.ResolveBoxedTypeName(context.X0DefIdx, x0Offset);
                context.Inst.BoxedTypeDefIndex = context.ResolveBoxedTypeDefIndex(context.X0DefIdx);
                if (string.IsNullOrEmpty(typeName) && ConsoleReporter.Verbose)
                {
                    ConsoleReporter.Debug($"BoxingClassifier: Failed to resolve boxed type name for typeDefIdx {context.Inst.BoxedTypeDefIndex} at idx={context.Index}");
                }
                if (!string.IsNullOrEmpty(typeName) && !typeName.Contains('['))
                    return $"box<{typeName}>";
            }
            // Variant B: x0 = xN (register move), trace xN
            else if (context.X0Def.Opcode == IrOpcode.Assign && context.X0Def.Sources.Length > 0 &&
                     context.X0Def.Sources[0].Kind == IrOperandKind.Register)
            {
                long srcReg = context.X0Def.Sources[0].Value;
                var (prev, prevIdx) = IrTracingUtils.FindDefinition(context.Insts, context.X0DefIdx, srcReg, 8);
                if (prev != null)
                {
                    if (prev.Opcode == IrOpcode.Load && prev.Sources.Length > 0 &&
                        prev.Sources[0].Kind == IrOperandKind.Memory)
                    {
                        long typeOffset = prev.Sources[0].Offset;
                        string typeName = context.ResolveBoxedTypeName(prevIdx, typeOffset);
                        context.Inst.BoxedTypeDefIndex = context.ResolveBoxedTypeDefIndex(prevIdx);
                        if (string.IsNullOrEmpty(typeName) && ConsoleReporter.Verbose)
                        {
                            ConsoleReporter.Debug($"BoxingClassifier: Failed to resolve boxed type name for typeDefIdx {context.Inst.BoxedTypeDefIndex} at idx={context.Index}");
                        }
                        if (!string.IsNullOrEmpty(typeName) && !typeName.Contains('['))
                            return $"box<{typeName}>";
                    }
                }
            }
        }
        return null;
    }
}
