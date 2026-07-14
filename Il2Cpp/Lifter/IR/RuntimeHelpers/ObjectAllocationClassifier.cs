using System;
using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Lifter.IR.RuntimeHelpers;

public sealed class ObjectAllocationClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.EffectiveX0Def != null && context.EffectiveX0Def.Opcode == IrOpcode.Load &&
            context.EffectiveX0Def.Sources.Length > 0 && context.EffectiveX0Def.Sources[0].Kind == IrOperandKind.Memory &&
            context.EffectiveX0Def.Sources[0].Offset == 0) // load [reg] - dereference pointer
        {
            string? ctorTypeName = null;
            for (int j = context.Index + 1; j < Math.Min(context.Index + 10, context.Insts.Count); j++)
            {
                var instJ = context.Insts[j];
                if (instJ.Opcode == IrOpcode.Call && instJ.Annotation is string ctorAnn &&
                    ctorAnn.Contains("::.ctor"))
                {
                    int sep = ctorAnn.IndexOf("::.ctor");
                    ctorTypeName = sep > 0 ? ctorAnn[..sep] : "?";
                    break;
                }
                if (instJ.Opcode == IrOpcode.TailCall && instJ.Annotation is string ctorAnnTail &&
                    ctorAnnTail.Contains("::.ctor"))
                {
                    int sep = ctorAnnTail.IndexOf("::.ctor");
                    ctorTypeName = sep > 0 ? ctorAnnTail[..sep] : "?";
                    break;
                }
            }

            string? addrAnnotation = context.FindAddrAnnotationForLoad(context.EffectiveX0DefIdx);
            bool isArrayType = false;
            if (addrAnnotation != null)
            {
                string typeName = IrDataResolver.ExtractTypeName(addrAnnotation);
                isArrayType = typeName.Contains('[');
                if (!isArrayType)
                {
                    return $"new {typeName}()";
                }
            }

            if (ctorTypeName != null && !isArrayType)
            {
                return $"new {ctorTypeName}()";
            }
        }
        return null;
    }
}
