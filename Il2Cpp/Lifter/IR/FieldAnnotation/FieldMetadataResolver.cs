using System;
using System.Collections.Generic;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Analysis.Resolve;
using Rosetta.Binary;
using Rosetta.Common;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Metadata;
using Rosetta.Model;
using Rosetta.Pipeline;
using Rosetta.Analysis.Utils;
using static Rosetta.Analysis.Utils.TypeAliasUtils;

namespace Rosetta.Lifter.IR.FieldAnnotation;

internal sealed class FieldMetadataResolver
{
    private readonly MetadataParser? _metadata;
    private readonly RegistrationResolver? _registration;
    private readonly TypeResolver? _typeResolver;
    private readonly TypeModel? _typeModel;

    // Per-type caches
    private readonly Dictionary<int, Dictionary<int, (string name, bool isStatic)>?> _fieldMapCache = new();
    private readonly Dictionary<int, Dictionary<int, string>?> _staticFieldMapCache = new();
    private readonly Dictionary<string, int> _typeDefIndexByName = new();
    private bool _typeNameCacheBuilt = false;

    public MetadataParser? Metadata => _metadata;
    public TypeModel? TypeModel => _typeModel;

    public FieldMetadataResolver(
        MetadataParser? metadata,
        RegistrationResolver? registration,
        TypeResolver? typeResolver,
        TypeModel? typeModel)
    {
        _metadata = metadata;
        _registration = registration;
        _typeResolver = typeResolver;
        _typeModel = typeModel;
    }

    public int FindTypeDefIndex(string typeName)
    {
        if (_metadata == null) return -1;
        if (!_typeNameCacheBuilt)
        {
            for (int i = 0; i < _metadata.TypeDefinitions.Length; i++)
            {
                var td = _metadata.TypeDefinitions[i];
                if (td.FullName != null)
                {
                    _typeDefIndexByName[td.FullName] = i;
                    if (td.Name != null && td.Name != td.FullName)
                        _typeDefIndexByName.TryAdd(td.Name, i);
                }
            }
            _typeNameCacheBuilt = true;
        }
        return _typeDefIndexByName.TryGetValue(typeName, out int idx) ? idx : -1;
    }

    public Dictionary<int, (string name, bool isStatic)>? GetFieldMap(int typeDefIndex)
    {
        if (typeDefIndex < 0) return null;
        if (!_fieldMapCache.TryGetValue(typeDefIndex, out var fieldMap))
        {
            if (_typeModel != null && _typeModel.FieldLayouts.TryGetValue(typeDefIndex, out var layout))
            {
                fieldMap = new Dictionary<int, (string, bool)>();
                foreach (var entry in layout.Fields)
                {
                    if (entry.Offset >= 0)
                        fieldMap.TryAdd(entry.Offset, (entry.Name, entry.IsStatic));
                }
                ResolveParentFieldsViaModel(fieldMap, typeDefIndex);
                if (fieldMap.Count == 0) fieldMap = null;
            }
            else if (_metadata != null && _registration != null)
            {
                fieldMap = BuildFieldMap(typeDefIndex);
            }
            _fieldMapCache[typeDefIndex] = fieldMap;
        }
        return fieldMap;
    }

    public Dictionary<int, string>? GetStaticFieldMap(int typeDefIndex)
    {
        if (typeDefIndex < 0) return null;
        if (!_staticFieldMapCache.TryGetValue(typeDefIndex, out var staticFieldMap))
        {
            if (_metadata != null && _registration != null)
                staticFieldMap = BuildStaticFieldMap(typeDefIndex);
            _staticFieldMapCache[typeDefIndex] = staticFieldMap;
        }
        return staticFieldMap;
    }

    internal int TraceStaticOwnerType(List<IrInstruction> insts, int staticFieldsLoadIdx, int fallbackTypeIdx)
    {
        if (_metadata == null) return fallbackTypeIdx;
        var staticLoad = insts[staticFieldsLoadIdx];
        if (staticLoad.Sources.Length == 0) return fallbackTypeIdx;

        long klassReg = staticLoad.Sources[0].Value;
        var (prev, prevIdx) = IrTracingUtils.FindDefinition(insts, staticFieldsLoadIdx, klassReg, 10);
        if (prev != null)
        {
            if (prev.Annotation != null && prev.Annotation.StartsWith("typeof("))
            {
                string typeName = prev.Annotation[7..^1];
                for (int t = 0; t < _metadata.TypeDefinitions.Length; t++)
                    if (_metadata.TypeDefinitions[t].FullName == typeName ||
                        _metadata.TypeDefinitions[t].Name == typeName)
                        return t;
            }
            if (prev.Opcode == IrOpcode.Load && prev.Sources.Length > 0 &&
                prev.Sources[0].Kind == IrOperandKind.Memory && prev.Sources[0].Offset == 0)
            {
                long vtableReg = prev.Sources[0].Value;
                var (prev2, _) = IrTracingUtils.FindDefinition(insts, prevIdx, vtableReg, prevIdx);
                if (prev2 != null)
                {
                    if (prev2.Annotation != null && prev2.Annotation.StartsWith("typeof("))
                    {
                        string tn = prev2.Annotation[7..^1];
                        for (int t = 0; t < _metadata.TypeDefinitions.Length; t++)
                            if (_metadata.TypeDefinitions[t].FullName == tn ||
                                _metadata.TypeDefinitions[t].Name == tn)
                                return t;
                    }
                    else
                    {
                        if (ConsoleReporter.Verbose)
                        {
                            ConsoleReporter.Debug($"[FIELDRESOLVER-DEBUG] TraceStaticOwnerType: prev2 annotation is '{prev2.Annotation}' (not typeof) for vtableReg {vtableReg}");
                        }
                    }
                }
                else
                {
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Debug($"[FIELDRESOLVER-DEBUG] TraceStaticOwnerType: prev2 is null for vtableReg {vtableReg}");
                    }
                }
            }
            else
            {
                if (ConsoleReporter.Verbose)
                {
                    ConsoleReporter.Debug($"[FIELDRESOLVER-DEBUG] TraceStaticOwnerType: prev opcode is {prev.Opcode}, annotation is '{prev.Annotation}', destination is {prev.Destination}");
                }
            }
        }
        else
        {
            if (ConsoleReporter.Verbose)
            {
                ConsoleReporter.Debug($"[FIELDRESOLVER-DEBUG] TraceStaticOwnerType: prev is null (no definition found for klassReg {klassReg} within 10 instructions)");
            }
        }

        string fallbackName = fallbackTypeIdx >= 0 && fallbackTypeIdx < _metadata.TypeDefinitions.Length 
            ? _metadata.TypeDefinitions[fallbackTypeIdx].FullName ?? _metadata.TypeDefinitions[fallbackTypeIdx].Name ?? "Unknown"
            : "Unknown";
        if (ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"[FIELDRESOLVER-DEBUG] TraceStaticOwnerType: falling back to type index {fallbackTypeIdx} ({fallbackName})");
        }

        return fallbackTypeIdx;
    }

    internal int TraceCallArgType(List<IrInstruction> insts, int callIdx, int fallbackTypeIdx)
    {
        if (_metadata == null) return fallbackTypeIdx;

        var (prev, prevIdx) = IrTracingUtils.FindDefinition(insts, callIdx, 0, 10);
        if (prev != null)
        {
            if (prev.Annotation != null && prev.Annotation.StartsWith("typeof("))
            {
                string typeName = prev.Annotation[7..^1];
                for (int t = 0; t < _metadata.TypeDefinitions.Length; t++)
                {
                    if (_metadata.TypeDefinitions[t].FullName == typeName ||
                        _metadata.TypeDefinitions[t].Name == typeName)
                        return t;
                }
            }
            if (prev.Opcode == IrOpcode.Load && prev.Sources.Length > 0)
            {
                long srcReg = prev.Sources[0].Value;
                var (prev2, _) = IrTracingUtils.FindDefinition(insts, prevIdx, srcReg, 8);
                if (prev2 != null && prev2.Annotation != null && prev2.Annotation.StartsWith("typeof("))
                {
                    string typeName = prev2.Annotation[7..^1];
                    for (int t = 0; t < _metadata.TypeDefinitions.Length; t++)
                    {
                        if (_metadata.TypeDefinitions[t].FullName == typeName ||
                            _metadata.TypeDefinitions[t].Name == typeName)
                            return t;
                    }
                }
                else if (prev2 != null)
                {
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Debug($"[FIELDRESOLVER-DEBUG] TraceCallArgType: prev2 annotation is '{prev2.Annotation}' (not typeof) for srcReg {srcReg}");
                    }
                }
                else
                {
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Debug($"[FIELDRESOLVER-DEBUG] TraceCallArgType: prev2 is null for srcReg {srcReg}");
                    }
                }
            }
            else
            {
                if (ConsoleReporter.Verbose)
                {
                    ConsoleReporter.Debug($"[FIELDRESOLVER-DEBUG] TraceCallArgType: prev opcode is {prev.Opcode}, annotation is '{prev.Annotation}'");
                }
            }
        }
        else
        {
            if (ConsoleReporter.Verbose)
            {
                ConsoleReporter.Debug($"[FIELDRESOLVER-DEBUG] TraceCallArgType: prev is null (no definition found for register 0 within 10 instructions)");
            }
        }

        string fallbackName = fallbackTypeIdx >= 0 && fallbackTypeIdx < _metadata.TypeDefinitions.Length 
            ? _metadata.TypeDefinitions[fallbackTypeIdx].FullName ?? _metadata.TypeDefinitions[fallbackTypeIdx].Name ?? "Unknown"
            : "Unknown";
        if (ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"[FIELDRESOLVER-DEBUG] TraceCallArgType: falling back to type index {fallbackTypeIdx} ({fallbackName})");
        }

        return fallbackTypeIdx;
    }

    internal Dictionary<int, (string name, bool isStatic)>? BuildFieldMap(int typeDefIndex)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"IrDataResolver: BuildFieldMap called for typeDefIndex={typeDefIndex}");
        if (_metadata == null || _registration == null) return null;
        if (typeDefIndex < 0 || typeDefIndex >= _metadata.TypeDefinitions.Length) return null;
        if (typeDefIndex >= _registration.FieldOffsets.Length) return null;

        var result = new Dictionary<int, (string, bool)>();
        ResolveFieldsForType(result, typeDefIndex);
        return result.Count > 0 ? result : null;
    }

    internal Dictionary<int, string>? BuildStaticFieldMap(int typeDefIndex)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"IrDataResolver: BuildStaticFieldMap called for typeDefIndex={typeDefIndex}");
        if (_metadata == null || _registration == null) return null;
        if (typeDefIndex < 0 || typeDefIndex >= _metadata.TypeDefinitions.Length) return null;
        if (typeDefIndex >= _registration.FieldOffsets.Length) return null;

        var typeDef = _metadata.TypeDefinitions[typeDefIndex];
        int[]? offsets = _registration.FieldOffsets[typeDefIndex];

        if (offsets == null || typeDef.FieldCount == 0) return null;

        var result = new Dictionary<int, string>();
        int fieldStart = typeDef.FieldStart;
        int fieldCount = Math.Min(typeDef.FieldCount, offsets.Length);

        for (int f = 0; f < fieldCount; f++)
        {
            int globalFieldIdx = fieldStart + f;
            if (globalFieldIdx >= _metadata.FieldDefinitions.Length) break;

            var fieldDef = _metadata.FieldDefinitions[globalFieldIdx];
            if (!fieldDef.IsStatic) continue;
            if (fieldDef.IsLiteral) continue;

            int byteOffset = offsets[f];
            if (byteOffset < 0 || byteOffset > 0x2000) continue;

            string fieldName = fieldDef.Name ?? $"static_field_{f}";

            if (_typeResolver != null)
            {
                try
                {
                    string resolvedType = _typeResolver.ResolveTypeName(fieldDef.TypeIndex);
                    string shortType = TypeAliasUtils.GetShortTypeName(resolvedType);
                    if (shortType.Length > 0)
                        fieldName = $"{fieldName}:{shortType}";
                }
                catch (Exception ex)
                {
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Warning($"[FIELDRESOLVER-WARN] Failed resolving type name for static field {fieldName} (TypeIdx={fieldDef.TypeIndex}): {ex.Message}");
                    }
                }
            }

            result.TryAdd(byteOffset, fieldName);
        }

        return result.Count > 0 ? result : null;
    }

    internal void ResolveFieldsForType(Dictionary<int, (string, bool)> map, int typeDefIndex)
    {
        if (_metadata == null || _registration == null) return;
        if (typeDefIndex < 0 || typeDefIndex >= _metadata.TypeDefinitions.Length) return;

        var typeDef = _metadata.TypeDefinitions[typeDefIndex];
        int[]? offsets = typeDefIndex < _registration.FieldOffsets.Length
            ? _registration.FieldOffsets[typeDefIndex]
            : null;

        if (offsets != null && typeDef.FieldCount > 0)
        {
            int fieldStart = typeDef.FieldStart;
            int fieldCount = Math.Min(typeDef.FieldCount, offsets.Length);

            for (int f = 0; f < fieldCount; f++)
            {
                int globalFieldIdx = fieldStart + f;
                if (globalFieldIdx >= _metadata.FieldDefinitions.Length) break;

                var fieldDef = _metadata.FieldDefinitions[globalFieldIdx];
                int byteOffset = offsets[f];
                if (!fieldDef.IsStatic && byteOffset == 0 && !typeDef.IsValueType) byteOffset = 16;

                if (byteOffset < 0 || byteOffset > 0x2000) continue;

                string fieldName = fieldDef.Name ?? $"field_{f}";
                bool isStatic = fieldDef.IsStatic;

                map.TryAdd(byteOffset, (fieldName, isStatic));
            }
        }

        if (typeDef.ParentIndex >= 0 && typeDef.ParentIndex != typeDefIndex)
        {
            if (_typeResolver != null)
            {
                try
                {
                    var parentType = _typeResolver.GetTypeByIndex(typeDef.ParentIndex);
                    if (parentType.HasValue &&
                        (parentType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_CLASS ||
                         parentType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE))
                    {
                        ResolveFieldsForType(map, parentType.Value.KlassIndex);
                    }
                }
                catch (Exception ex)
                {
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Warning($"[FIELDRESOLVER-WARN] Failed resolving parent type for type index {typeDefIndex} (ParentIdx={typeDef.ParentIndex}): {ex.Message}");
                    }
                }
            }
        }
    }

    internal void ResolveParentFieldsViaModel(Dictionary<int, (string, bool)> map, int typeDefIndex)
    {
        if (_typeModel == null || _metadata == null || _typeResolver == null) return;
        if (typeDefIndex < 0 || typeDefIndex >= _metadata.TypeDefinitions.Length) return;

        var typeDef = _metadata.TypeDefinitions[typeDefIndex];
        if (typeDef.ParentIndex < 0) return;

        Rosetta.Binary.Il2CppType? parentType = null;
        try
        {
            parentType = _typeResolver.GetTypeByIndex(typeDef.ParentIndex);
        }
        catch (Exception ex)
        {
            if (ConsoleReporter.Verbose)
            {
                ConsoleReporter.Warning($"[FIELDRESOLVER-WARN] Failed resolving parent type in ResolveParentFieldsViaModel for typeDefIndex={typeDefIndex} (ParentIdx={typeDef.ParentIndex}): {ex.Message}");
            }
        }

        if (parentType == null || !parentType.HasValue) return;
        if (parentType.Value.TypeEnum is not (Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE))
            return;

        int parentKlassIdx = parentType.Value.KlassIndex;
        if (parentKlassIdx == typeDefIndex || parentKlassIdx < 0) return;

        if (_typeModel.FieldLayouts.TryGetValue(parentKlassIdx, out var parentLayout))
        {
            foreach (var entry in parentLayout.Fields)
            {
                if (entry.Offset >= 0)
                    map.TryAdd(entry.Offset, (entry.Name, entry.IsStatic));
            }
        }

        ResolveParentFieldsViaModel(map, parentKlassIdx);
    }

    internal string LookupFieldTypeHint(int typeDefIndex, int byteOffset)
    {
        if (_metadata == null || _typeResolver == null) return "";

        if (_typeModel != null && _typeModel.FieldLayouts.TryGetValue(typeDefIndex, out var layout))
        {
            var fe = layout.GetFieldInfoAtOffset(byteOffset);
            if (fe != null)
                return TypeAliasUtils.GetShortTypeName(fe.TypeName);
        }

        if (_registration != null &&
            typeDefIndex >= 0 && typeDefIndex < _metadata.TypeDefinitions.Length &&
            typeDefIndex < _registration.FieldOffsets.Length)
        {
            var typeDef = _metadata.TypeDefinitions[typeDefIndex];
            int[]? offsets = _registration.FieldOffsets[typeDefIndex];
            if (offsets != null)
            {
                int fieldStart = typeDef.FieldStart;
                int fieldCount = Math.Min(typeDef.FieldCount, offsets.Length);
                for (int f = 0; f < fieldCount; f++)
                {
                    int globalFieldIdx = fieldStart + f;
                    if (globalFieldIdx < _metadata.FieldDefinitions.Length)
                    {
                        var fieldDef = _metadata.FieldDefinitions[globalFieldIdx];
                        if (offsets[f] == byteOffset || (byteOffset == 16 && offsets[f] == 0 && !typeDef.IsValueType && !fieldDef.IsStatic))
                        {
                            try
                            {
                                string typeName = _typeResolver.ResolveTypeName(fieldDef.TypeIndex);
                                string shortType = TypeAliasUtils.GetShortTypeName(typeName);
                                if (shortType != "")
                                    return shortType;
                            }
                            catch (Exception ex)
                            {
                                if (ConsoleReporter.Verbose)
                                {
                                    ConsoleReporter.Warning($"[FIELDRESOLVER-WARN] Failed resolving type hint for field {fieldDef.Name} (TypeIdx={fieldDef.TypeIndex}): {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
        }

        if (typeDefIndex >= 0 && typeDefIndex < _metadata.TypeDefinitions.Length)
        {
            var typeDef = _metadata.TypeDefinitions[typeDefIndex];
            if (typeDef.ParentIndex >= 0)
            {
                Rosetta.Binary.Il2CppType? parentType = null;
                try
                {
                    parentType = _typeResolver.GetTypeByIndex(typeDef.ParentIndex);
                }
                catch (Exception ex)
                {
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Warning($"[FIELDRESOLVER-WARN] Failed resolving parent type hint for typeDefIndex={typeDefIndex} (ParentIdx={typeDef.ParentIndex}): {ex.Message}");
                    }
                }

                if (parentType != null && parentType.HasValue &&
                    parentType.Value.TypeEnum is Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
                {
                    int parentIdx = parentType.Value.KlassIndex;
                    if (parentIdx != typeDefIndex && parentIdx >= 0)
                        return LookupFieldTypeHint(parentIdx, byteOffset);
                }
            }
        }

        return "";
    }


    internal static bool IsArrayLengthContext(Dictionary<long, IrDataResolver.ObjectAlias> aliases, long baseReg)
    {
        return aliases.TryGetValue(baseReg, out var alias) && alias.Type.IsArray;
    }

    internal static bool IsArrayElementContext(Dictionary<long, IrDataResolver.ObjectAlias> aliases, long baseReg)
    {
        return aliases.TryGetValue(baseReg, out var alias) && alias.Type.IsArray;
    }

    internal static bool IsStringLengthContext(Dictionary<long, IrDataResolver.ObjectAlias> aliases, long baseReg)
    {
        if (aliases.TryGetValue(baseReg, out var alias))
        {
            return alias.Type.BaseType == "System.String" || alias.Type.BaseType == "string" || alias.Type.BaseType == "String";
        }
        return false;
    }

    internal static bool IsVtableLoadContext(List<IrInstruction> insts, int idx)
    {
        var currentInst = insts[idx];
        long destReg = -1;
        if (currentInst.Destination.HasValue &&
            currentInst.Destination.Value.Kind == IrOperandKind.Register)
            destReg = currentInst.Destination.Value.Value;

        for (int j = idx + 1; j < Math.Min(idx + 10, insts.Count); j++)
        {
            if (insts[j].Opcode == IrOpcode.Load && insts[j].Sources.Length > 0 &&
                insts[j].Sources[0].Kind == IrOperandKind.Memory &&
                insts[j].Sources[0].Offset >= 0x100 &&
                insts[j].Sources[0].Value == destReg)
                return true;
            if (destReg >= 0 && insts[j].Destination is IrOperand destOp &&
                destOp.Kind == IrOperandKind.Register &&
                destOp.Value == destReg)
                break;
        }
        return false;
    }
}
