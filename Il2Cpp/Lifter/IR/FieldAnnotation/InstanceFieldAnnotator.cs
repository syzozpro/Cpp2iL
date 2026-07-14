using System;
using System.Linq;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Model;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR.FieldAnnotation;

internal sealed class InstanceFieldAnnotator : IFieldOffsetAnnotator
{
    private bool IsCustomStruct(FieldMetadataResolver metadataResolver, string typeName)
    {
        if (metadataResolver.TypeModel == null) return false;
        if (metadataResolver.TypeModel.FieldLayoutsByTypeName.TryGetValue(typeName, out int typeDefIdx))
        {
            var typeDef = metadataResolver.TypeModel.GetTypeDef(typeDefIdx);
            if (typeDef != null && typeDef.IsStruct)
            {
                if (typeDef.Namespace != null && (typeDef.Namespace == "System" || typeDef.Namespace.StartsWith("System.")))
                    return false;
                return true;
            }
        }
        return false;
    }

    private string? ResolveNestedField(FieldMetadataResolver metadataResolver, string typeName, long offset, bool isRoot, bool goDeeperOnZeroOffset, int accessSize, out string fieldType, System.Collections.Generic.HashSet<string>? visited = null)
    {
        fieldType = "";
        if (metadataResolver.TypeModel == null)
            return null;

        visited ??= new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        if (visited.Contains(typeName))
        {
            return null;
        }

        visited.Add(typeName);
        try
        {
            var layout = metadataResolver.TypeModel.GetLayoutForTypeName(typeName);
            if (layout == null)
            {
                if (ConsoleReporter.Verbose)
                {
                    ConsoleReporter.Warning($"[INSTANCEANNOTATOR-WARN] Field layout is null for type: {typeName}");
                }
                return null;
            }

            long adjustedOffset = offset;
            if (!isRoot)
            {
                adjustedOffset = offset + 16;
            }

            FieldLayout.FieldEntry? bestField = null;
            foreach (var field in layout.Fields)
            {
                if (field.IsStatic || field.Offset < 0)
                    continue;

                if (field.Offset <= adjustedOffset)
                {
                    if (bestField == null || field.Offset > bestField.Offset)
                    {
                        bestField = field;
                    }
                }
            }

            if (bestField == null)
            {
                if (ConsoleReporter.Verbose)
                {
                    ConsoleReporter.Warning($"[INSTANCEANNOTATOR-WARN] No field found in type {typeName} at offset 0x{adjustedOffset:X}");
                }
                return null;
            }

            string fieldName = bestField.Name;
            fieldType = bestField.TypeName;

            long remainingOffset = adjustedOffset - bestField.Offset;
            if (remainingOffset == 0)
            {
                bool isStruct = IsCustomStruct(metadataResolver, bestField.TypeName);

                if (isStruct && goDeeperOnZeroOffset)
                {
                    int structSize = 8;
                    if (metadataResolver.TypeModel != null && metadataResolver.TypeModel.TryGetMetadataSize(bestField.TypeName, out int calculatedSize, out _))
                    {
                        structSize = calculatedSize;
                    }

                    if (accessSize < structSize)
                    {
                        var subFieldPath = ResolveNestedField(metadataResolver, bestField.TypeName, 0, false, true, accessSize, out var subType, visited);
                        if (subFieldPath != null)
                        {
                            fieldType = subType;
                            return $"{fieldName}.{subFieldPath}";
                        }
                    }
                }

                return fieldName;
            }

            bool isFieldStruct = IsCustomStruct(metadataResolver, bestField.TypeName);

            if (isFieldStruct)
            {
                var subFieldPath = ResolveNestedField(metadataResolver, bestField.TypeName, remainingOffset, false, goDeeperOnZeroOffset, accessSize, out var subType, visited);
                if (subFieldPath != null)
                {
                    fieldType = subType;
                    return $"{fieldName}.{subFieldPath}";
                }
            }

            return null;
        }
        finally
        {
            visited.Remove(typeName);
        }
    }

    public bool Annotate(FieldAnnotationContext context)
    {
        if (context.Offset < 0x400 && context.ObjectAliases.TryGetValue(context.BaseReg, out var aliasInfo))
        {
            int intOffset = (int)(aliasInfo.BaseOffset + context.Offset);
            if (intOffset < 8) return false;

            int accessSize = 4;
            if (context.Inst.Sources.Length > 0 && context.Inst.Sources[0].Kind == IrOperandKind.Memory)
            {
                accessSize = context.Inst.Sources[0].BitWidth / 8;
            }

            string targetTypeName = aliasInfo.IsThis ? (context.Method.DeclaringType ?? "Object") : aliasInfo.Type.OriginalName;
            bool goDeeper = (context.Inst.Opcode != IrOpcode.Add);
            string? fieldPath = ResolveNestedField(context.MetadataResolver, targetTypeName, intOffset, true, goDeeper, accessSize, out string typeHint);

            if (fieldPath != null)
            {
                string prefix = aliasInfo.IsThis ? "this." : "->";
                string annotation = typeHint != ""
                    ? $"{prefix}{fieldPath}:{typeHint}"
                    : $"{prefix}{fieldPath}";

                if (context.Inst.Opcode == IrOpcode.Load)
                     context.LoadedFieldType = typeHint;

                if (context.Inst.Opcode == IrOpcode.Store && context.Inst.Sources.Length >= 2 &&
                    context.Inst.Sources[1].BitWidth >= 64 &&
                    (context.Inst.Sources[1].Kind == IrOperandKind.Register ||
                     context.Inst.Sources[1].Kind == IrOperandKind.FpRegister) &&
                    !fieldPath.Contains("."))
                {
                    FieldLayout? layout = context.MetadataResolver.TypeModel?.GetLayoutForTypeName(targetTypeName);
                    if (layout != null)
                    {
                        int totalBytes = context.Inst.Sources[1].BitWidth / 8;
                        int primaryFieldSize = Rosetta.Common.TypeUtils.GetFieldSizeFromType(typeHint, context.MetadataResolver.TypeModel);
                        for (int subOff = primaryFieldSize; subOff < 4 && subOff < totalBytes; subOff++)
                        {
                            int subOffset = intOffset + subOff;
                            var subField = layout.Fields.FirstOrDefault(f => f.Offset == subOffset && !f.IsStatic);
                            if (subField != null)
                            {
                                string subType = subField.TypeName;
                                string subPart = subType != ""
                                    ? $"{prefix}{subField.Name}:{subType}"
                                    : $"{prefix}{subField.Name}";
                                annotation += $"+{subPart}";
                                int subSize = Rosetta.Common.TypeUtils.GetFieldSizeFromType(subType, context.MetadataResolver.TypeModel);
                                subOff += subSize - 1;
                            }
                        }

                        int packedSlots = totalBytes / 4;
                        for (int slot = 1; slot < packedSlots; slot++)
                        {
                            int slotBase = intOffset + slot * 4;
                            int byteInSlot = 0;
                            while (byteInSlot < 4)
                            {
                                int adjOffset = slotBase + byteInSlot;
                                var adjField = layout.Fields.FirstOrDefault(f => f.Offset == adjOffset && !f.IsStatic);
                                if (adjField != null)
                                {
                                    string adjType = adjField.TypeName;
                                    string adjPart = adjType != ""
                                        ? $"{prefix}{adjField.Name}:{adjType}"
                                        : $"{prefix}{adjField.Name}";
                                    annotation += $"+{adjPart}";
                                    int adjSize = Rosetta.Common.TypeUtils.GetFieldSizeFromType(adjType, context.MetadataResolver.TypeModel);
                                    byteInSlot += Math.Max(adjSize, 1);
                                }
                                else
                                {
                                    byteInSlot++;
                                }
                            }
                        }
                    }
                }

                context.Inst.Annotation = annotation;
                context.IncrementFieldsAnnotated();
                return true;
            }
            else
            {
                if (ConsoleReporter.Verbose)
                {
                    ConsoleReporter.Warning($"[INSTANCEANNOTATOR-WARN] Could not resolve field path for type '{targetTypeName}' at offset 0x{intOffset:X} (accessSize={accessSize}, baseOffset={aliasInfo.BaseOffset}, context.Offset={context.Offset})");
                }
            }
        }
        return false;
    }
}
