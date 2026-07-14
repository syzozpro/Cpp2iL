using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR.FieldAnnotation;

internal sealed class StaticFieldAnnotator : IFieldOffsetAnnotator
{
    public bool Annotate(FieldAnnotationContext context)
    {
        if (context.Offset >= 0 && context.Offset < 0x400)
        {
            // Check if base register holds a static_fields or thread_static_data pointer
            bool isStaticAccess = false;
            bool isThreadStatic = false;
            int ownerTypeDefIdx = context.Method.TypeDefIndex;

            var (prev, prevIdx) = IrTracingUtils.FindDefinition(context.Insts, context.Index, context.BaseReg, 12);
            if (prev != null)
            {
                if (prev.Annotation == "static_fields")
                {
                    isStaticAccess = true;
                    ownerTypeDefIdx = context.MetadataResolver.TraceStaticOwnerType(context.Insts, prevIdx, context.Method.TypeDefIndex);
                }
                else if (prev.Opcode == IrOpcode.Call && prev.Annotation != null &&
                         prev.Annotation.Contains("thread_static_data"))
                {
                    isStaticAccess = true;
                    isThreadStatic = true;
                    ownerTypeDefIdx = context.MetadataResolver.TraceCallArgType(context.Insts, prevIdx, context.Method.TypeDefIndex);
                }
            }

            if (isStaticAccess)
            {
                var sMap = (ownerTypeDefIdx == context.Method.TypeDefIndex)
                    ? context.StaticFieldMap
                    : context.MetadataResolver.GetStaticFieldMap(ownerTypeDefIdx);

                if (sMap == null)
                {
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Warning($"[STATICANNOTATOR-WARN] Static field map is null for type index {ownerTypeDefIdx} at offset 0x{context.Offset:X}");
                    }
                }

                string prefix = isThreadStatic ? "[ThreadStatic] " : "";

                if (sMap != null && sMap.TryGetValue((int)context.Offset, out string? staticFieldName))
                {
                    string typeName = context.Method.DeclaringType ?? "Type";
                    var metadata = context.MetadataResolver.Metadata;
                    if (ownerTypeDefIdx != context.Method.TypeDefIndex && metadata != null &&
                        ownerTypeDefIdx >= 0 && ownerTypeDefIdx < metadata.TypeDefinitions.Length)
                    {
                        typeName = metadata.TypeDefinitions[ownerTypeDefIdx].Name ?? typeName;
                    }
                    string annotation = $"{prefix}{typeName}.{staticFieldName}";

                    // For 64-bit stores: if the main field is NOT a 64-bit type,
                    // this is a packed store covering two 32-bit fields.
                    // Append the adjacent field at offset+4 with '+' separator.
                    int colonIdx = staticFieldName.LastIndexOf(':');
                    string? mainTypeHint = colonIdx > 0 ? staticFieldName[(colonIdx + 1)..] : null;
                    bool is64BitField = mainTypeHint is "double" or "long" or "ulong";
                    if (!is64BitField && context.Inst.Opcode == IrOpcode.Store && context.Inst.Sources.Length >= 2)
                    {
                        var storeVal = context.Inst.Sources[1];
                        bool is64BitStore = storeVal.Kind == IrOperandKind.Register && storeVal.BitWidth == 64;
                        if (is64BitStore)
                        {
                            int adjOffset = (int)context.Offset + 4;
                            if (sMap.TryGetValue(adjOffset, out string? adjFieldName))
                                annotation += $"+{typeName}.{adjFieldName}";
                        }
                    }

                    context.Inst.Annotation = annotation;
                    context.IncrementFieldsAnnotated();
                    return true;
                }
                else if (sMap != null)
                {
                    // Mid-struct offset: find nearest lower field (within 12 bytes = Vector3 size)
                    string? nearestField = null;
                    long nearestDistance = long.MaxValue;
                    foreach (var kv in sMap)
                    {
                        long dist = context.Offset - kv.Key;
                        if (dist > 0 && dist <= 12 && dist < nearestDistance)
                        {
                            nearestField = kv.Value;
                            nearestDistance = dist;
                        }
                    }
                    string typeName = context.Method.DeclaringType ?? "Type";
                    var metadata = context.MetadataResolver.Metadata;
                    if (ownerTypeDefIdx != context.Method.TypeDefIndex && metadata != null &&
                        ownerTypeDefIdx >= 0 && ownerTypeDefIdx < metadata.TypeDefinitions.Length)
                        typeName = metadata.TypeDefinitions[ownerTypeDefIdx].Name ?? typeName;

                    if (nearestField == null)
                    {
                        if (ConsoleReporter.Verbose)
                        {
                            string keys = string.Join(", ", sMap.Keys.Select(k => $"0x{k:X}"));
                            ConsoleReporter.Warning($"[STATICANNOTATOR-WARN] Could not find static field or near field at offset 0x{context.Offset:X} in type index {ownerTypeDefIdx} ({typeName}). Available offsets: [{keys}]");
                        }
                    }

                    context.Inst.Annotation = nearestField != null
                        ? $"{prefix}{typeName}.{nearestField}"
                        : $"{prefix}static[0x{context.Offset:X}]";
                    context.IncrementFieldsAnnotated();
                    return true;
                }
            }
        }
        return false;
    }
}
