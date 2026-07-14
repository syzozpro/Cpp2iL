using System;
using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Lifter.IR.RuntimeHelpers;

public sealed class ChainedCallClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null && context.X0Def.Opcode == IrOpcode.Call)
        {
            string? prevCallAnnotation = context.X0Def.Annotation;
            if (prevCallAnnotation != null)
            {
                if (prevCallAnnotation.Contains("__cxa_begin_catch"))
                    return "exception_filter";
                if (prevCallAnnotation.Contains("__cxa_end_catch") ||
                    prevCallAnnotation.Contains("__cxa_throw"))
                    return "exception_cleanup";
                if (prevCallAnnotation.Contains("string_intern"))
                    return "string_intern_resolve";
                if (prevCallAnnotation.Contains("::.ctor"))
                    return "post_ctor_init";
                if (prevCallAnnotation.Contains("il2cpp_runtime_helper") ||
                    prevCallAnnotation.Contains("box<") ||
                    prevCallAnnotation.Contains("new ") ||
                    prevCallAnnotation.Contains("gc_write_barrier"))
                    return "runtime_chain";
                if (prevCallAnnotation.Contains("Debug::Log") ||
                    prevCallAnnotation.Contains("String::Concat") ||
                    prevCallAnnotation.Contains("String::Format"))
                    return "class_init";
                if (prevCallAnnotation.Contains("AsyncTaskMethodBuilder") ||
                    prevCallAnnotation.Contains("AsyncVoidMethodBuilder"))
                    return "async_builder_op";
                if (prevCallAnnotation.Contains("Delegate::Combine") ||
                    prevCallAnnotation.Contains("Delegate::Remove"))
                    return "delegate_combine";
                if (prevCallAnnotation.Contains("Action"))
                    return "delegate_create";
                
                return "post_call_helper";
            }
            return "chained_helper";
        }
        return null;
    }
}
