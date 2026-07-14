using System;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR.FieldAnnotation;

internal sealed class StaticFieldsPointerAnnotator : IFieldOffsetAnnotator
{
    public bool Annotate(FieldAnnotationContext context)
    {
        // Il2CppClass static fields pointer: [klass + 0xB8]
        if (context.Offset == 0xB8 && context.Inst.Opcode == IrOpcode.Load)
        {
            context.Inst.Annotation = "static_fields";
            context.IncrementFieldsAnnotated();
            return true;
        }
        return false;
    }
}

internal sealed class VTableLoadAnnotator : IFieldOffsetAnnotator
{
    public bool Annotate(FieldAnnotationContext context)
    {
        // Il2CppObject klass pointer: [obj + 0x0]
        if (context.Offset == 0 && context.Inst.Opcode == IrOpcode.Load)
        {
            if (context.ObjectAliases.TryGetValue(context.BaseReg, out var alias))
            {
                if (FieldMetadataResolver.IsVtableLoadContext(context.Insts, context.Index))
                {
                    context.Inst.SemanticTag = IrSemanticTag.VTableLoad;
                    context.IncrementFieldsAnnotated();
                    return true;
                }
                else
                {
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Debug($"[VTABLEANNOTATOR-WARN] Offset 0 load at index {context.Index} in method {context.Method.MethodName} failed vtable context validation. BaseReg X{context.BaseReg} (alias type: \"{alias.Type.OriginalName}\", baseOffset: 0x{alias.BaseOffset:X}). Instruction: {context.Inst.Opcode} Ann: '{context.Inst.Annotation ?? "null"}'");
                    }
                }
            }
        }
        return false;
    }
}

internal sealed class HeuristicLengthAnnotator : IFieldOffsetAnnotator
{
    public bool Annotate(FieldAnnotationContext context)
    {
        if (context.Inst.Opcode != IrOpcode.Load && context.Inst.Opcode != IrOpcode.Store)
        {
            return false;
        }

        if (context.Offset == 0x18)
        {
            if (FieldMetadataResolver.IsArrayLengthContext(context.ObjectAliases, context.BaseReg))
            {
                context.Inst.Annotation = ".Length";
                context.IncrementFieldsAnnotated();
                return true;
            }
        }

        if (context.Offset == 0x20)
        {
            if (FieldMetadataResolver.IsArrayElementContext(context.ObjectAliases, context.BaseReg))
            {
                context.Inst.Annotation = "[0]";
                context.IncrementFieldsAnnotated();
                return true;
            }
        }

        if (context.Offset == 0x10)
        {
            if (FieldMetadataResolver.IsStringLengthContext(context.ObjectAliases, context.BaseReg))
            {
                context.Inst.Annotation = ".Length";
                context.IncrementFieldsAnnotated();
                return true;
            }
        }

        return false;
    }
}
