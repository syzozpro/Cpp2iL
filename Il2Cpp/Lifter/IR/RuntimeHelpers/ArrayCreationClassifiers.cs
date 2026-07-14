using System;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR.RuntimeHelpers;

public sealed class ArrayCreationClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X1Def != null && context.X1Def.Opcode == IrOpcode.LoadImmediate &&
            context.X1Def.Sources.Length > 0)
        {
            long arrayLen = context.X1Def.Sources[0].Value;

            if (context.EffectiveX0Def != null && context.EffectiveX0Def.Opcode == IrOpcode.Load)
            {
                string? addrAnnotation = context.FindAddrAnnotationForLoad(context.EffectiveX0DefIdx);

                if (addrAnnotation == null && context.EffectiveX0Def.Sources.Length > 0 &&
                    context.EffectiveX0Def.Sources[0].Kind == IrOperandKind.Memory &&
                    context.EffectiveX0Def.Sources[0].Offset == 0)
                {
                    long baseReg = context.EffectiveX0Def.Sources[0].Value;
                    for (int k = context.EffectiveX0DefIdx - 1; k >= 0; k--)
                    {
                        var prev = context.Insts[k];
                        if (prev.Destination.HasValue &&
                            prev.Destination.Value.Kind == IrOperandKind.Register &&
                            prev.Destination.Value.Value == baseReg &&
                            prev.Annotation != null &&
                            prev.Annotation.StartsWith("typeof("))
                        {
                            addrAnnotation = prev.Annotation;
                            break;
                        }
                    }
                }

                string typeName = addrAnnotation != null ? IrDataResolver.ExtractTypeName(addrAnnotation) : "?";
                if (typeName == "?" && ConsoleReporter.Verbose)
                {
                    ConsoleReporter.Debug($"ArrayCreationClassifier: Unresolved element type name for array creation at idx={context.Index}");
                }
                if (typeName.EndsWith("[]"))
                    typeName = typeName[..^2];
                return $"new {typeName}[{arrayLen}]";
            }
        }
        return null;
    }
}

public sealed class MultiDimArrayClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null && context.X0Def.Opcode == IrOpcode.Load &&
            context.X1Def != null && context.X1Def.Opcode == IrOpcode.Add &&
            context.X1Def.Sources.Length >= 2 &&
            context.X1Def.Sources[0].Kind == IrOperandKind.Register &&
            IrRegisterConstants.IsStackPointer(context.X1Def.Sources[0].Value)) // x1/R1 = SP + offset
        {
            string? addrAnn = context.FindAddrAnnotationForLoad(context.X0DefIdx);
            if (addrAnn == null && context.X0Def.Sources.Length > 0 &&
                context.X0Def.Sources[0].Kind == IrOperandKind.Memory &&
                context.X0Def.Sources[0].Offset == 0)
            {
                long baseReg = context.X0Def.Sources[0].Value;
                for (int k = context.X0DefIdx - 1; k >= 0; k--)
                {
                    var prev = context.Insts[k];
                    if (prev.Destination.HasValue &&
                        prev.Destination.Value.Kind == IrOperandKind.Register &&
                        prev.Destination.Value.Value == baseReg &&
                        prev.Annotation != null &&
                        prev.Annotation.StartsWith("typeof("))
                    {
                        addrAnn = prev.Annotation;
                        break;
                    }
                }
            }

            if (addrAnn != null)
            {
                string mdTypeName = IrDataResolver.ExtractTypeName(addrAnn);
                int bracketIdx = mdTypeName.IndexOf('[');
                string baseType = bracketIdx >= 0 ? mdTypeName[..bracketIdx] : mdTypeName;

                long spOffset = context.X1Def.Sources[1].Value;
                var dims = context.ExtractMultiDimBounds(context.Index, spOffset);
                if (dims != null)
                    return $"new {baseType}{dims}";
                else
                    return $"new {mdTypeName}";
            }
        }
        return null;
    }
}
