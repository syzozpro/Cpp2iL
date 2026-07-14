// TypeModel — the single source of truth for all type, field, method, and string data.
// Every other component (lifter, resolver, emitter) queries this model.
// Built once during pipeline initialization from metadata + binary registration.

using System;
using System.Collections.Generic;
using Rosetta.Binary;
using Rosetta.Metadata;
using Rosetta.Pipeline;

namespace Rosetta.Model;

/// <summary>
/// Unified type model that resolves metadata addresses to their semantic meaning.
/// This is the central data layer — all lifter/analysis/emitter code queries this.
/// </summary>
public sealed partial class TypeModel
{
    // ── Core Dependencies ──────────────────────────────────────────────────
    private readonly MetadataParser _metadata;
    private readonly RegistrationResolver _registration;
    private readonly TypeResolver _typeResolver;

    // ── Resolved Lookup Tables ─────────────────────────────────────────────

    /// <summary>
    /// TypeDefinition index → field layout (field offset → field info).
    /// Used to resolve *([x8 + 0x48]) into field names.
    /// </summary>
    public Dictionary<int, FieldLayout> FieldLayouts { get; } = new();

    /// <summary>
    /// Reverse lookup: TypeName → TypeDefinition index.
    /// O(1) alternative to scanning FieldLayouts by TypeName.
    /// </summary>
    public Dictionary<string, int> FieldLayoutsByTypeName { get; } = new();

    /// <summary>
    /// Method index → full signature (return type, param types, param names).
    /// Used to identify MethodInfo null args and reconstruct call expressions.
    /// </summary>
    public Dictionary<int, MethodSignature> Signatures { get; } = new();

    /// <summary>
    /// Set of all static properties in the form "FullName.PropertyName" (e.g. "UnityEngine.Vector3.zero")
    /// Used to safely strip backing field suffixes.
    /// </summary>
    public HashSet<string> StaticProperties { get; } = new();

    /// <summary>
    /// Quick lookup: "Namespace.Type::MethodName" → IsStatic.
    /// Built during BuildMethodSignatures(). Used by AstBuilder to determine
    /// whether x0 is 'this' (instance) or a real argument (static).
    /// </summary>
    private readonly Dictionary<string, bool> _methodStaticLookup = new();

    /// <summary>
    /// Quick lookup: "Namespace.Type::MethodName" → parameter list.
    /// Built during BuildMethodSignatures(). Used by AstBuilder for type-aware arg formatting.
    /// </summary>
    private readonly Dictionary<string, List<MethodSignature.ParamEntry>> _methodParamLookup = new();
    private readonly Dictionary<int, List<MethodSignature.ParamEntry>> _methodParamLookupByIdx = new();

    /// <summary>
    /// Quick lookup: "Namespace.Type::MethodName" → MethodSignature.
    /// Built during BuildMethodSignatures().
    /// </summary>
    private readonly Dictionary<string, MethodSignature> _methodSignatureLookup = new();

    /// <summary>
    /// Type full name → VTableLayout (slot index → method name).
    /// Built from metadata vtable section during Build().
    /// </summary>
    private readonly Dictionary<string, VTableLayout> _vtableByType = new();

    /// <summary>
    /// Il2CppType index → resolved C# type name.
    /// Cached from TypeResolver for fast lookup.
    /// </summary>
    public Dictionary<int, string> TypeNamesByIndex { get; } = new();
    private readonly Dictionary<int, EnumInfo> _enumInfoByTypeDefIndex = new();

    /// <summary>
    /// The globally computed base byte offset for the vtable array in Il2CppClass.
    /// Calculated once deterministically and shared across all parallel lifters.
    /// </summary>
    public long VTableBaseOffset { get; set; } = -1;

    // ── Build Statistics ───────────────────────────────────────────────────
    public int FieldLayoutsBuilt { get; private set; }
    public int SignaturesBuilt { get; private set; }
    public int TypeNamesResolved { get; private set; }

    // ── Construction ───────────────────────────────────────────────────────

    public TypeModel(MetadataParser metadata, RegistrationResolver registration, TypeResolver typeResolver)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _typeResolver = typeResolver ?? throw new ArgumentNullException(nameof(typeResolver));
    }

    /// <summary>
    /// Build all lookup tables. Call once after metadata + binary are loaded.
    /// </summary>
    public void Build()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace("TypeModel: Build started");
        BuildFieldLayouts();
        BuildMethodSignatures();
        BuildTypeNameIndex();
        BuildEnumInfo();
        BuildVTableLayouts();
        BuildStaticProperties();
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace("TypeModel: Build finished");
    }

    // ── Query Methods ──────────────────────────────────────────────────────

    /// <summary>
    /// Given a type def index and a byte offset, return the field name.
    /// Returns null if offset doesn't match any known field.
    /// </summary>
    public string? ResolveFieldAtOffset(int typeDefIndex, int byteOffset)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"TypeModel: ResolveFieldAtOffset called for typeDefIndex={typeDefIndex}, offset=0x{byteOffset:X}");
        if (!FieldLayouts.TryGetValue(typeDefIndex, out var layout))
            return null;
        return layout.GetFieldAtOffset(byteOffset);
    }

    /// <summary>
    /// Given a type def index and a byte offset, return full field info.
    /// </summary>
    public FieldLayout.FieldEntry? ResolveFieldInfoAtOffset(int typeDefIndex, int byteOffset)
    {
        if (!FieldLayouts.TryGetValue(typeDefIndex, out var layout))
            return null;
        var entry = layout.GetFieldInfoAtOffset(byteOffset);
        if (entry != null) return entry;
        
        // Handle value type memory padding (IL2CPP metadata includes 0x10 object header for structs)
        var firstInstance = System.Linq.Enumerable.FirstOrDefault(layout.Fields, f => !f.IsStatic);
        if (firstInstance != null && firstInstance.Offset >= 16 && byteOffset < firstInstance.Offset)
        {
            return layout.GetFieldInfoAtOffset(byteOffset + 16);
        }
        return null;
    }

    /// <summary>
    /// Resolve a static field by its byte offset within the static_fields block.
    /// Returns the field entry if found, null otherwise.
    /// </summary>
    public FieldLayout.FieldEntry? ResolveStaticFieldAtOffset(int typeDefIndex, int byteOffset)
    {
        if (!FieldLayouts.TryGetValue(typeDefIndex, out var layout))
            return null;
        foreach (var field in layout.Fields)
        {
            if (field.IsStatic && field.Offset == byteOffset)
                return field;
        }
        return null;
    }

    /// <summary>
    /// Get the full method signature for a method definition index.
    /// </summary>
    public MethodSignature? GetSignature(int methodIndex)
    {
        Signatures.TryGetValue(methodIndex, out var sig);
        return sig;
    }

    /// <summary>
    /// Check if a method is static by its annotation string (e.g., "System.String::Concat").
    /// Returns null if the method isn't found in metadata (caller should use a fallback).
    /// </summary>
    public bool? IsMethodStatic(string annotation)
    {
        if (_methodStaticLookup.TryGetValue(annotation, out var isStatic))
            return isStatic;
        return null;
    }

    /// <summary>
    /// Get the parameter list for a method by its annotation string (e.g., "UnityEngine.GameObject::SetActive").
    /// Returns null if the method isn't found in metadata.
    /// </summary>
    public IReadOnlyList<MethodSignature.ParamEntry>? GetMethodParameters(string annotation)
    {
        if (_methodParamLookup.TryGetValue(annotation, out var parameters))
            return parameters;
        return null;
    }

    /// <summary>
    /// Get the MethodSignature for a method by its annotation string.
    /// </summary>
    public MethodSignature? GetMethodSignature(string annotation)
    {
        if (_methodSignatureLookup.TryGetValue(annotation, out var sig))
            return sig;
        return null;
    }

    /// <summary>
    /// Get the C# type name for an Il2CppType index.
    /// </summary>
    public string GetTypeName(int typeIndex)
    {
        if (TypeNamesByIndex.TryGetValue(typeIndex, out var name))
            return name;
        return _typeResolver.ResolveTypeName(typeIndex);
    }

    /// <summary>
    /// Get the TypeDefinition for a type def index.
    /// </summary>
    public TypeDefinition? GetTypeDef(int typeDefIndex)
    {
        if (typeDefIndex < 0 || typeDefIndex >= _metadata.TypeDefinitions.Length)
            return null;
        return _metadata.TypeDefinitions[typeDefIndex];
    }

    /// <summary>
    /// Convert an Il2CppType index from metadata usage into its exact TypeDefinition index.
    /// </summary>
    public int ResolveTypeDefIndexFromTypeIndex(int typeIndex)
    {
        var type = _typeResolver.GetTypeByIndex(typeIndex);
        if (!type.HasValue)
            return -1;

        return type.Value.TypeEnum is Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_CLASS
            or Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE
            or Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_ENUM
            ? type.Value.KlassIndex
            : -1;
    }

    /// <summary>
    /// Get the MethodDefinition for a method index.
    /// </summary>
    public MethodDefinition? GetMethod(int methodIndex)
    {
        if (methodIndex < 0 || methodIndex >= _metadata.MethodDefinitions.Length)
            return null;
        return _metadata.MethodDefinitions[methodIndex];
    }

    /// <summary>
    /// Check if a method is a property getter or setter by consulting the TypeDefinition's Property definitions.
    /// This avoids fragile string matching (e.g. name.StartsWith("get_")).
    /// </summary>
    public bool IsPropertyAccessor(int methodIndex)
    {
        var md = GetMethod(methodIndex);
        if (md == null) return false;

        int typeIdx = md.DeclaringTypeIndex;
        if (typeIdx < 0 || typeIdx >= _metadata.TypeDefinitions.Length) return false;

        var td = _metadata.TypeDefinitions[typeIdx];
        if (td.PropertyCount == 0) return false;

        int methodOffset = methodIndex - td.MethodStart;

        for (int i = 0; i < td.PropertyCount; i++)
        {
            int propIdx = td.PropertyStart + i;
            if (propIdx >= 0 && propIdx < _metadata.PropertyDefinitions.Length)
            {
                var prop = _metadata.PropertyDefinitions[propIdx];
                if (prop.Get == methodOffset || prop.Set == methodOffset)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the actual property name from metadata if the method is an accessor.
    /// Returns null if the method is not a property accessor.
    /// </summary>
    public string? GetPropertyNameFromAccessor(int methodIndex, out bool isGetter)
    {
        isGetter = false;
        var md = GetMethod(methodIndex);
        if (md == null) return null;

        int typeIdx = md.DeclaringTypeIndex;
        if (typeIdx < 0 || typeIdx >= _metadata.TypeDefinitions.Length) return null;

        var td = _metadata.TypeDefinitions[typeIdx];
        if (td.PropertyCount == 0) return null;

        int methodOffset = methodIndex - td.MethodStart;

        for (int i = 0; i < td.PropertyCount; i++)
        {
            int propIdx = td.PropertyStart + i;
            if (propIdx >= 0 && propIdx < _metadata.PropertyDefinitions.Length)
            {
                var prop = _metadata.PropertyDefinitions[propIdx];
                if (prop.Get == methodOffset)
                {
                    isGetter = true;
                    return prop.Name;
                }
                if (prop.Set == methodOffset)
                {
                    isGetter = false;
                    return prop.Name;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolve a field's type with a generic context.
    /// </summary>
    public string ResolveFieldTypeWithContext(int typeDefIndex, int byteOffset,
        Dictionary<string, string>? genericContext)
    {
        if (!FieldLayouts.TryGetValue(typeDefIndex, out var layout))
            return "?";

        var field = layout.GetFieldInfoAtOffset(byteOffset);
        if (field == null)
            return "?";

        string typeName = field.TypeName;

        if (genericContext != null && genericContext.Count > 0)
        {
            if (genericContext.TryGetValue(typeName, out var concrete))
                return concrete;
        }

        return typeName;
    }

    // ── Build Helpers ──────────────────────────────────────────────────────

    private void BuildMethodSignatures()
    {
        int built = 0;
        for (int methodIdx = 0; methodIdx < _metadata.MethodDefinitions.Length; methodIdx++)
        {
            var method = _metadata.MethodDefinitions[methodIdx];
            string retTypeName = "void";
            int retHfaSize = 0;
            string[]? retHfaFields = null;
            if (method.ReturnTypeIndex >= 0)
            {
                try
                {
                    retTypeName = _typeResolver.ResolveTypeName(method.ReturnTypeIndex);
                    retHfaSize = _typeResolver.GetHfaSize(method.ReturnTypeIndex);
                    retHfaFields = _typeResolver.GetHfaFieldNames(method.ReturnTypeIndex);
                }
                catch (Exception ex)
                {
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Warning($"[TYPEMODEL-WARN] Failed resolving return type for method {method.Name ?? $"Method_{methodIdx}"} (TypeIdx={method.ReturnTypeIndex}): {ex.Message}");
                    }
                }
            }

            var sig = new MethodSignature
            {
                Name = method.Name ?? $"Method_{methodIdx}",
                ReturnTypeName = retTypeName,
                ReturnHfaSize = retHfaSize,
                ReturnHfaFieldNames = retHfaFields,
                IsStatic = (method.Flags & 0x0010) != 0,
                ParameterCount = method.ParameterCount,
            };

            for (int p = 0; p < method.ParameterCount; p++)
            {
                int paramIdx = method.ParameterStart + p;
                if (paramIdx >= _metadata.ParameterDefinitions.Length) break;

                var param = _metadata.ParameterDefinitions[paramIdx];
                
                string paramTypeName = "?";
                int paramHfaSize = 0;
                Rosetta.Binary.Il2CppType? il2cppType = null;
                if (param.TypeIndex >= 0)
                {
                    try
                    {
                        paramTypeName = _typeResolver.ResolveTypeName(param.TypeIndex);
                        paramHfaSize = _typeResolver.GetHfaSize(param.TypeIndex);
                        il2cppType = _typeResolver.GetTypeByIndex(param.TypeIndex);
                    }
                    catch (Exception ex)
                    {
                        if (ConsoleReporter.Verbose)
                        {
                            ConsoleReporter.Warning($"[TYPEMODEL-WARN] Failed resolving parameter {p} type for method {sig.Name} (TypeIdx={param.TypeIndex}): {ex.Message}");
                        }
                    }
                }
                
                sig.Parameters.Add(new MethodSignature.ParamEntry
                {
                    Name = param.Name ?? $"arg{p}",
                    TypeName = paramTypeName,
                    TypeIndex = param.TypeIndex,
                    HfaSize = paramHfaSize,
                    IsByRef = il2cppType?.ByRef ?? false,
                    IsOut = il2cppType != null && il2cppType.Value.ByRef && (il2cppType.Value.Attrs & 0x0002) != 0,
                    IsIn  = il2cppType != null && il2cppType.Value.ByRef && (il2cppType.Value.Attrs & 0x0001) != 0
                });
            }

            Signatures[methodIdx] = sig;
            built++;

            if (method.Name != null && method.DeclaringTypeIndex >= 0 && method.DeclaringTypeIndex < _metadata.TypeDefinitions.Length)
            {
                var typeDef = _metadata.TypeDefinitions[method.DeclaringTypeIndex];
                string key = $"{typeDef.FullName}::{method.Name}";
                _methodStaticLookup.TryAdd(key, sig.IsStatic);
                _methodParamLookup.TryAdd(key, sig.Parameters);
                _methodSignatureLookup.TryAdd(key, sig);
            }
        }
        SignaturesBuilt = built;
    }

    private void BuildTypeNameIndex()
    {
        int resolved = 0;
        var types = _registration.Types;
        for (int i = 0; i < types.Length; i++)
        {
            try
            {
                TypeNamesByIndex[i] = _typeResolver.ResolveTypeName(i);
            }
            catch (Exception ex)
            {
                if (ConsoleReporter.Verbose)
                {
                    ConsoleReporter.Warning($"[TYPEMODEL-WARN] Failed resolving type name for type index {i}: {ex.Message}");
                }
                TypeNamesByIndex[i] = $"?type_{i}";
            }
            resolved++;
        }
        TypeNamesResolved = resolved;
    }

    private void BuildStaticProperties()
    {
        for (int t = 0; t < _metadata.TypeDefinitions.Length; t++)
        {
            var td = _metadata.TypeDefinitions[t];
            if (td.PropertyCount == 0) continue;

            string typeName = td.FullName;
            for (int p = 0; p < td.PropertyCount; p++)
            {
                int propIdx = td.PropertyStart + p;
                if (propIdx >= 0 && propIdx < _metadata.PropertyDefinitions.Length)
                {
                    var prop = _metadata.PropertyDefinitions[propIdx];
                    bool isStatic = false;
                    
                    if (prop.Get >= 0)
                    {
                        var md = _metadata.MethodDefinitions[td.MethodStart + prop.Get];
                        isStatic = (md.Flags & 0x0010) != 0;
                    }
                    else if (prop.Set >= 0)
                    {
                        var md = _metadata.MethodDefinitions[td.MethodStart + prop.Set];
                        isStatic = (md.Flags & 0x0010) != 0;
                    }

                    if (isStatic && prop.Name != null)
                    {
                        StaticProperties.Add($"{typeName}.{prop.Name}");
                        if (td.Name != null)
                        {
                            StaticProperties.Add($"{td.Name}.{prop.Name}");
                        }
                    }
                }
            }
        }
    }
}
