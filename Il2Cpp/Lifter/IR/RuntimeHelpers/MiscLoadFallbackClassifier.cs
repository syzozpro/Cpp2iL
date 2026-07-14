using System;
using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Lifter.IR.RuntimeHelpers;

public sealed class MiscLoadFallbackClassifier : IRuntimeHelperClassifier
{
    public string? Classify(RuntimeHelperContext context)
    {
        if (context.X0Def != null && context.X0Def.Opcode == IrOpcode.Load)
        {
            // Check if load source has any useful annotation
            if (context.X0Def.Annotation != null)
            {
                if (context.X0Def.Annotation.Contains("static_fields"))
                    return "static_field_access";
                if (context.X0Def.Annotation.Contains("\""))
                    return "string_intern";
                if (context.X0Def.Annotation.Contains("MethodInfo") ||
                    context.X0Def.Annotation.Contains("MethodRef"))
                {
                    string typeName = IrDataResolver.ExtractTypeName(context.X0Def.Annotation);
                    return $"method_resolve({typeName})";
                }
                return "metadata_resolve";
            }
            
            // Unannotated load — classify by offset pattern
            long offset = context.X0Def.Sources.Length > 0 ? context.X0Def.Sources[0].Offset : -1;
            return offset switch
            {
                0xB8 => "static_field_access",
                0x88 => "type_info_load",
                0x30 => "metadata_slot",
                _ => "runtime_helper",
            };
        }
        return null;
    }
}
