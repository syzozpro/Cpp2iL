using System;
using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Lifter.IR.RuntimeHelpers;

public sealed class NullX0Classifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def == null)
        {
            if (context.Inst.Opcode == IrOpcode.Branch || context.Inst.Opcode == IrOpcode.TailCall)
                return "tail_call_helper";
            return "runtime_helper_misc";
        }
        return null;
    }
}

public sealed class GcWriteBarrierClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X1Def != null && context.X1Def.Opcode == IrOpcode.Load &&
            context.X1Def.Sources.Length > 0 && context.X1Def.Sources[0].Kind == IrOperandKind.Memory &&
            context.X1Def.Sources[0].Offset == 0x40)
        {
            bool isAllocatorX0 = context.EffectiveX0Def != null && context.EffectiveX0Def.Opcode == IrOpcode.Load &&
                                 context.EffectiveX0Def.Sources.Length > 0 && context.EffectiveX0Def.Sources[0].Kind == IrOperandKind.Memory &&
                                 context.EffectiveX0Def.Sources[0].Offset == 0;
            
            if (!isAllocatorX0)
            {
                return "gc_write_barrier";
            }
        }
        return null;
    }
}

public sealed class StringInternClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null && context.X0Def.Opcode == IrOpcode.Assign)
        {
            bool isStringSource = true;
            if (context.X0Def.Sources.Length > 0 && context.X0Def.Sources[0].Kind == IrOperandKind.Register)
            {
                long srcReg = context.X0Def.Sources[0].Value;
                var (prev, _) = IrTracingUtils.FindDefinition(context.Insts, context.X0DefIdx, srcReg, 8);
                if (prev != null)
                {
                    if (prev.Annotation != null &&
                        (prev.Annotation.Contains("typeof(") ||
                         prev.Annotation.Contains("type(") ||
                         prev.Annotation.StartsWith("metadata")))
                    {
                        isStringSource = false;
                    }
                }
            }
            return isStringSource ? "string_intern" : "box<?>";
        }
        return null;
    }
}

public sealed class GenericMethodDispatchClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null && context.X0Def.Opcode == IrOpcode.Load &&
            context.X0Def.Sources.Length > 0 && context.X0Def.Sources[0].Kind == IrOperandKind.Memory &&
            context.X0Def.Annotation != null && context.X0Def.Annotation.Contains("MethodRef"))
        {
            string methodName = IrDataResolver.ExtractTypeName(context.X0Def.Annotation);
            return $"generic_invoke({methodName})";
        }
        return null;
    }
}

public sealed class StaticFieldWriteBarrierClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null && context.X0Def.Opcode == IrOpcode.Load &&
            context.X0Def.Sources.Length > 0 && context.X0Def.Sources[0].Kind == IrOperandKind.Memory &&
            context.X0Def.Sources[0].Offset == 0xB8)
        {
            return "gc_write_barrier_static";
        }
        return null;
    }
}

public sealed class AsyncStateMachineClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null && context.X0Def.Opcode == IrOpcode.Or)
        {
            return "async_await_continue";
        }
        return null;
    }
}

public sealed class ClassInitClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null && context.X0Def.Opcode == IrOpcode.LoadAddress)
        {
            return "class_init";
        }
        return null;
    }
}

public sealed class CatchAllClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null)
        {
            return context.X0Def.Opcode switch
            {
                IrOpcode.FAdd or IrOpcode.FSub or IrOpcode.FMul or IrOpcode.FDiv => "float_box",
                _ => "runtime_helper_misc",
            };
        }
        return "runtime_helper_misc";
    }
}
