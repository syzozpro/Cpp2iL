using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Pipeline;

namespace Rosetta.Model;

public sealed partial class TypeModel
{
    private readonly ConcurrentDictionary<string, FieldLayout> _genericLayoutCache = new();
    private readonly ConcurrentDictionary<string, (int Size, int Alignment)> _genericSizeCache = new();
    private readonly HashSet<int> _mergedTypes = new();
    [System.ThreadStatic]
    private static System.Collections.Generic.HashSet<string>? _resolvingSizes;

    public FieldLayout? GetLayoutForTypeName(string typeName)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"TypeModel: GetLayoutForTypeName called for {typeName}");
        // Direct non-generic lookup
        if (FieldLayoutsByTypeName.TryGetValue(typeName, out int typeDefIdx))
        {
            if (FieldLayouts.TryGetValue(typeDefIdx, out var layout))
                return layout;
            return null;
        }

        // Generic instantiation lookup
        if (typeName.Contains("<") && typeName.EndsWith(">"))
        {
            if (_genericLayoutCache.TryGetValue(typeName, out var cachedLayout))
                return cachedLayout;

            int bracketIdx = typeName.IndexOf('<');
            if (bracketIdx > 0)
            {
                string openName = typeName[..bracketIdx];
                string argsStr = typeName.Substring(bracketIdx + 1, typeName.Length - bracketIdx - 2);
                var args = ParseGenericArgs(argsStr);
                string typeDefName = $"{openName}`{args.Count}";
                
                if (FieldLayoutsByTypeName.TryGetValue(typeDefName, out int openTypeDefIdx))
                {
                    var newLayout = GetOrCreateGenericLayout(openTypeDefIdx, args);
                    _genericLayoutCache[typeName] = newLayout;
                    return newLayout;
                }
            }
        }

        return null;
    }

    public FieldLayout GetOrCreateGenericLayout(int typeDefIndex, IReadOnlyList<string> genericArgs)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"TypeModel: GetOrCreateGenericLayout called for typeDefIndex={typeDefIndex}");
        var typeDef = _metadata.TypeDefinitions[typeDefIndex];
        var layout = new FieldLayout($"{typeDef.FullName}<{string.Join(", ", genericArgs)}>");

        if (!FieldLayouts.TryGetValue(typeDefIndex, out var openLayout))
            return layout;

        var genericContext = new Dictionary<string, string>();
        if (typeDef.GenericContainerIndex >= 0)
        {
            var genContainer = _metadata.GenericContainers[typeDef.GenericContainerIndex];
            for (int i = 0; i < genContainer.Count && i < genericArgs.Count; i++)
            {
                var param = _metadata.GenericParameters[genContainer.ParameterStart + i];
                string paramName = param.Name ?? $"T{i}";
                genericContext[paramName] = genericArgs[i];
            }
        }

        int currentOffset = typeDef.IsValueType ? 0 : Rosetta.Common.Constants.ObjectHeaderSize;

        foreach (var openField in openLayout.Fields)
        {
            if (openField.IsStatic) continue;

            string resolvedTypeName = openField.TypeName;
            if (genericContext.TryGetValue(openField.TypeName, out var concrete))
                resolvedTypeName = concrete;

            int size;
            int alignment;

            if (TryGetMetadataSize(resolvedTypeName, out int calculatedSize, out int calculatedAlign))
            {
                size = calculatedSize;
                alignment = calculatedAlign;
            }
            else
            {
                if (ConsoleReporter.Verbose)
                {
                    ConsoleReporter.Warning($"[TYPEMODEL-WARN] Size unknown for generic field type: {resolvedTypeName}. Defaulting to pointer size.");
                }
                size = 8;
                alignment = 8;
            }

            if (currentOffset % alignment != 0)
                currentOffset += alignment - (currentOffset % alignment);

            layout.AddField(new FieldLayout.FieldEntry
            {
                Name = openField.Name,
                Offset = currentOffset,
                TypeName = resolvedTypeName,
                IsStatic = false,
                GlobalIndex = openField.GlobalIndex
            });

            currentOffset += size;
        }

        return layout;
    }

    public bool TryGetMetadataSize(string typeName, out int size, out int alignment)
    {
        size = 8;
        alignment = 8;

        _resolvingSizes ??= new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        if (!_resolvingSizes.Add(typeName))
        {
            return false;
        }

        try
        {
            // 1. Fixed ECMA Primitives
            switch (typeName)
            {
                case "System.Boolean": case "bool":
                case "System.Byte": case "byte":
                case "System.SByte": case "sbyte":
                    size = 1; alignment = 1; return true;
                case "System.Int16": case "short":
                case "System.UInt16": case "ushort":
                case "System.Char": case "char":
                    size = 2; alignment = 2; return true;
                case "System.Int32": case "int":
                case "System.UInt32": case "uint":
                case "System.Single": case "float":
                    size = 4; alignment = 4; return true;
                case "System.Int64": case "long":
                case "System.UInt64": case "ulong":
                case "System.Double": case "double":
                    size = 8; alignment = 8; return true;
                case "System.IntPtr": case "nint":
                case "System.UIntPtr": case "nuint":
                    size = 8; alignment = 8; return true;
                case "System.String": case "string":
                case "System.Object": case "object":
                    size = 8; alignment = 8; return true;
            }

            // Arrays and generic parameters like T[] or int[] are always pointer-sized references
            if (typeName.EndsWith("[]") || typeName.EndsWith("*"))
            {
                size = 8; alignment = 8; return true;
            }

            size = 8;
            alignment = 8;

            // Try exact type name lookup first
            if (FieldLayoutsByTypeName.TryGetValue(typeName, out int typeDefIndex))
            {
                var typeDef = _metadata.TypeDefinitions[typeDefIndex];
                
                // Reference types are pointer-sized
                if (!typeDef.IsValueType)
                {
                    size = 8;
                    alignment = 8;
                    return true;
                }

                // Enums inherit their underlying primitive size
                if (typeDef.IsEnum)
                {
                    if (FieldLayouts.TryGetValue(typeDefIndex, out var layout))
                    {
                        var valueField = layout.Fields.FirstOrDefault(f => f.Name == "value__");
                        if (valueField != null)
                            return TryGetMetadataSize(valueField.TypeName, out size, out alignment);
                    }
                    size = 4;
                    alignment = 4;
                    return true;
                }

                // Query native typeDefSizes from the registration binary
                if (_registration.TypeDefSizes != null && typeDefIndex < _registration.TypeDefSizes.Length)
                {
                    var sizes = _registration.TypeDefSizes[typeDefIndex];
                    if (sizes.instance_size > 0)
                    {
                        int structSize = (int)sizes.instance_size;
                        if (structSize >= Rosetta.Common.Constants.ObjectHeaderSize)
                            structSize -= Rosetta.Common.Constants.ObjectHeaderSize;
                        
                        if (structSize == 0) structSize = 1; // Empty structs
                        
                        size = structSize;
                        alignment = (size >= 8 || size == 0) ? 8 : (size >= 4 ? 4 : size);
                        return true;
                    }
                }

                // Fallback: dynamically calculate struct size from field layout offsets
                if (FieldLayouts.TryGetValue(typeDefIndex, out var fallbackLayout))
                {
                    int maxOffset = -1;
                    string? maxFieldType = null;
                    foreach (var f in fallbackLayout.Fields)
                    {
                        if (!f.IsStatic && f.Offset > maxOffset)
                        {
                            maxOffset = f.Offset;
                            maxFieldType = f.TypeName;
                        }
                    }
                    if (maxOffset >= 16)
                    {
                        int lastFieldSize = 8;
                        if (maxFieldType != null && TryGetMetadataSize(maxFieldType, out int resolvedFieldSize, out _))
                            lastFieldSize = resolvedFieldSize;
                        int computedSize = maxOffset + lastFieldSize - Rosetta.Common.Constants.ObjectHeaderSize;
                        if (computedSize > 0)
                        {
                            size = computedSize;
                            alignment = (size >= 8) ? 8 : (size >= 4 ? 4 : size);
                            return true;
                        }
                    }
                }

                return false;
            }

            // Generic structs like GenericMultiple<int, string>
            if (typeName.Contains("<") && typeName.EndsWith(">"))
            {
                if (_genericSizeCache.TryGetValue(typeName, out var cachedSize))
                {
                    size = cachedSize.Size;
                    alignment = cachedSize.Alignment;
                    return true;
                }

                int bracketIdx = typeName.IndexOf('<');
                if (bracketIdx > 0)
                {
                    string openName = typeName[..bracketIdx];
                    string argsStr = typeName.Substring(bracketIdx + 1, typeName.Length - bracketIdx - 2);
                    var args = ParseGenericArgs(argsStr);
                    string typeDefName = $"{openName}`{args.Count}";
                    
                    if (FieldLayoutsByTypeName.TryGetValue(typeDefName, out int openTypeDefIdx))
                    {
                        var typeDef = _metadata.TypeDefinitions[openTypeDefIdx];
                        if (!typeDef.IsValueType)
                        {
                            size = 8;
                            alignment = 8;
                            _genericSizeCache[typeName] = (size, alignment);
                            return true;
                        }

                        var genericLayout = GetLayoutForTypeName(typeName);
                        if (genericLayout == null) return false;

                        int structAlignment = 1;
                        int currentOffset = 0;

                        foreach (var field in genericLayout.Fields)
                        {
                            if (field.IsStatic) continue;

                            int fieldSize = 8;
                            int fieldAlignment = 8;
                            TryGetMetadataSize(field.TypeName, out fieldSize, out fieldAlignment);
                            
                            if (fieldAlignment > structAlignment)
                                structAlignment = fieldAlignment;

                            if (currentOffset % fieldAlignment != 0)
                                currentOffset += fieldAlignment - (currentOffset % fieldAlignment);

                            currentOffset += fieldSize;
                        }

                        if (currentOffset % structAlignment != 0)
                            currentOffset += structAlignment - (currentOffset % structAlignment);

                        size = currentOffset == 0 ? 1 : currentOffset;
                        alignment = structAlignment;
                        
                        _genericSizeCache[typeName] = (size, alignment);
                        return true;
                    }
                }
            }

            return false;
        }
        finally
        {
            _resolvingSizes.Remove(typeName);
        }
    }

    private static List<string> ParseGenericArgs(string argsStr)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < argsStr.Length; i++)
        {
            if (argsStr[i] == '<') depth++;
            else if (argsStr[i] == '>') depth--;
            else if (argsStr[i] == ',' && depth == 0)
            {
                result.Add(argsStr.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        if (start < argsStr.Length)
            result.Add(argsStr.Substring(start).Trim());
        return result;
    }

    private void BuildFieldLayouts()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace("TypeModel: BuildFieldLayouts started");
        int built = 0;
        for (int typeIdx = 0; typeIdx < _metadata.TypeDefinitions.Length; typeIdx++)
        {
            var typeDef = _metadata.TypeDefinitions[typeIdx];

            int[]? offsets = null;
            if (typeIdx < _registration.FieldOffsets.Length)
                offsets = _registration.FieldOffsets[typeIdx]; 

            var layout = new FieldLayout(typeDef.FullName);
            bool foundInstanceField = false;

            for (int f = 0; f < typeDef.FieldCount; f++)
            {
                int fieldGlobalIdx = typeDef.FieldStart + f;
                if (fieldGlobalIdx >= _metadata.FieldDefinitions.Length) break;

                var field = _metadata.FieldDefinitions[fieldGlobalIdx];

                int offset = -1;
                if (offsets != null && f < offsets.Length)
                    offset = offsets[f];

                if (!field.IsStatic && offset <= 0 && !foundInstanceField)
                {
                    foundInstanceField = true;
                    offset = typeDef.IsValueType ? 0 : 16;
                }

                string typeName = "?";
                Rosetta.Common.Il2CppTypeEnum elementType = Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_END;
                if (field.TypeIndex >= 0)
                {
                    try
                    {
                        typeName = _typeResolver.ResolveTypeName(field.TypeIndex);
                        var il2cppType = _typeResolver.GetTypeByIndex(field.TypeIndex);
                        if (il2cppType.HasValue)
                            elementType = il2cppType.Value.TypeEnum;
                    }
                    catch (Exception ex)
                    {
                        if (ConsoleReporter.Verbose)
                        {
                            ConsoleReporter.Warning($"[TYPEMODEL-WARN] Failed resolving field type for field {field.Name} (TypeIdx={field.TypeIndex}) in class {typeDef.FullName}: {ex.Message}");
                        }
                    }
                }

                layout.AddField(new FieldLayout.FieldEntry
                {
                    Name = field.Name ?? $"field_{f}",
                    Offset = offset,
                    TypeName = typeName,
                    IsStatic = field.IsStatic,
                    GlobalIndex = fieldGlobalIdx,
                    TypeIndex = field.TypeIndex,
                    ElementType = elementType
                });
            }

            FieldLayouts[typeIdx] = layout;
            FieldLayoutsByTypeName.TryAdd(typeDef.FullName, typeIdx);
            built++;
        }

        _mergedTypes.Clear();
        for (int typeIdx = 0; typeIdx < _metadata.TypeDefinitions.Length; typeIdx++)
        {
            MergeInheritedFields(typeIdx);
        }

        FieldLayoutsBuilt = built;
    }

    private void MergeInheritedFields(int typeIdx)
    {
        if (!_mergedTypes.Add(typeIdx)) return;

        var typeDef = _metadata.TypeDefinitions[typeIdx];
        if (typeDef.ParentIndex >= 0)
        {
            var parentType = _typeResolver.GetTypeByIndex(typeDef.ParentIndex);
            if (parentType.HasValue &&
                parentType.Value.TypeEnum is Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
            {
                int parentIdx = parentType.Value.KlassIndex;
                if (parentIdx >= 0 && parentIdx != typeIdx)
                {
                    MergeInheritedFields(parentIdx);

                    if (FieldLayouts.TryGetValue(typeIdx, out var layout) &&
                        FieldLayouts.TryGetValue(parentIdx, out var parentLayout))
                    {
                        foreach (var pf in parentLayout.Fields)
                        {
                            if (!layout.Fields.Any(f => f.Offset == pf.Offset && f.Name == pf.Name))
                            {
                                layout.AddField(pf);
                            }
                        }
                    }
                }
            }
        }
    }
}
