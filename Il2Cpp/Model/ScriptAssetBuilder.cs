using System.Collections.Generic;
using Rosetta.Metadata;
using Rosetta.Binary;

namespace Rosetta.Model;

/// <summary>
/// Factory for building ScriptAsset instances from raw metadata.
/// Populates all metadata-driven fields (identity, classification, fields,
/// properties, events, generics, inheritance).
/// Method analysis results are NOT populated here — that happens in later stages.
/// </summary>
public static class ScriptAssetBuilder
{
    public static ScriptAsset Build(int typeIndex, TypeDefinition td, MetadataParser metadata, TypeResolver? typeResolver, ScriptAsset? parent)
    {
        var asset = new ScriptAsset
        {
            TypeIndex = typeIndex,
            Name = td.Name ?? $"Type_{typeIndex}",
            Namespace = td.Namespace ?? "",
            TypeDef = td,
            Flags = td.Flags,
        };

        PopulateGenerics(asset, td, metadata, parent ?? asset);
        PopulateInheritance(asset, td, metadata, typeResolver, parent ?? asset);
        PopulateFields(asset, td, metadata, typeResolver, parent ?? asset);
        PopulateProperties(asset, td, metadata, typeResolver, parent ?? asset);
        PopulateEvents(asset, td, metadata, typeResolver, parent ?? asset);
        PopulateMethods(asset, td, metadata, typeResolver, parent ?? asset);

        return asset;
    }

    // ─── Generics ────────────────────────────────────────────────────────────

    private static void PopulateGenerics(ScriptAsset asset, TypeDefinition td, MetadataParser metadata, ScriptAsset parent)
    {
        if (td.GenericContainerIndex < 0 ||
            td.GenericContainerIndex >= metadata.GenericContainers.Length)
            return;

        var container = metadata.GenericContainers[td.GenericContainerIndex];
        for (int i = 0; i < container.Count; i++)
        {
            int paramIdx = container.ParameterStart + i;
            string name = paramIdx >= 0 && paramIdx < metadata.GenericParameters.Length
                ? metadata.GenericParameters[paramIdx].Name ?? $"T{i}"
                : $"T{i}";
            asset.GenericParameterNames.Add(name);
        }
    }

    // ─── Inheritance ─────────────────────────────────────────────────────────

    private static void PopulateInheritance(ScriptAsset asset, TypeDefinition td, MetadataParser metadata, TypeResolver? typeResolver, ScriptAsset parent)
    {
        if (typeResolver == null) return;

        if (td.ParentIndex >= 0 && !td.IsEnum)
        {
            string baseName = typeResolver.ResolveTypeName(td.ParentIndex);
            if (!IsImplicitBase(baseName, td))
            {
                asset.BaseTypeName = Common.TypeUtils.CleanTypeName(baseName, out string baseNS);
                AddUsings(parent, baseNS);
            }
        }

        if (td.InterfacesCount > 0)
        {
            for (int i = 0; i < td.InterfacesCount; i++)
            {
                int slot = td.InterfacesStart + i;
                if (slot < 0 || slot >= metadata.Interfaces.Length) continue;
                int ifaceTypeIdx = metadata.Interfaces[slot];
                string ifaceName = typeResolver.ResolveTypeName(ifaceTypeIdx);
                asset.InterfaceNames.Add(Common.TypeUtils.CleanTypeName(ifaceName, out string interfaceNS));
                AddUsings(parent, interfaceNS);
            }
        }
    }

    // ─── Fields ──────────────────────────────────────────────────────────────

    private static void PopulateFields(ScriptAsset asset, TypeDefinition td, MetadataParser metadata, TypeResolver? typeResolver, ScriptAsset parent)
    {
        for (int f = 0; f < td.FieldCount; f++)
        {
            int fieldIdx = td.FieldStart + f;
            if (fieldIdx < 0 || fieldIdx >= metadata.FieldDefinitions.Length) continue;
            var fd = metadata.FieldDefinitions[fieldIdx];

            // Get the raw resolved name BEFORE CleanTypeName strips namespace
            string rawTypeName = typeResolver != null
                ? typeResolver.ResolveTypeName(fd.TypeIndex)
                : "object";

            // Extract namespace from the raw name (e.g. "UnityEngine.AudioSource" → "UnityEngine")
            string fieldTypeNamespace = "";
            string cleaned = rawTypeName;
            if (!string.IsNullOrEmpty(rawTypeName))
            {
                // Handle array types: "UnityEngine.AudioClip[]" → base is "UnityEngine.AudioClip"
                string baseName = rawTypeName;
                int bracket = baseName.IndexOf('[');
                if (bracket > 0) baseName = baseName[..bracket];

                // Handle generics: "System.Collections.Generic.List<UnityEngine.AudioClip>" → skip
                int generic = baseName.IndexOf('<');
                if (generic > 0) baseName = baseName[..generic];

                int lastDot = baseName.LastIndexOf('.');
                if (lastDot > 0)
                    fieldTypeNamespace = baseName[..lastDot];

                cleaned = Common.TypeUtils.CleanTypeName(rawTypeName);
            }

            asset.Fields.Add(new ScriptAsset.FieldInfo
            {
                GlobalIndex = fieldIdx,
                Name = fd.Name ?? $"field_{f}",
                TypeName = cleaned,
                TypeNamespace = fieldTypeNamespace,
                IsStatic = fd.IsStatic,
                IsLiteral = fd.IsLiteral,
                IsReadOnly = fd.IsReadOnly,
                AccessFlags = fd.FieldAccess,
                FieldDef = fd,
            });

            AddUsings(parent, fieldTypeNamespace);
        }
    }

    // ─── Properties ──────────────────────────────────────────────────────────

    private static void PopulateProperties(ScriptAsset asset, TypeDefinition td, MetadataParser metadata, TypeResolver? typeResolver, ScriptAsset parent)
    {
        var fieldNameSet = new HashSet<string>();
        foreach (var fi in asset.Fields) fieldNameSet.Add(fi.Name);

        for (int p = 0; p < td.PropertyCount; p++)
        {
            int propIdx = td.PropertyStart + p;
            if (propIdx < 0 || propIdx >= metadata.PropertyDefinitions.Length) continue;

            var prop = metadata.PropertyDefinitions[propIdx];
            string propName = prop.Name ?? $"Property_{p}";
            bool hasGet = prop.Get >= 0;
            bool hasSet = prop.Set >= 0;
            string? getterName = null;
            string? setterName = null;
            string propType = "object";
            string getterAccess = "public ";
            string setterAccess = "public ";
            bool isStatic = false;

            if (hasGet)
            {
                int getMethodIdx = td.MethodStart + prop.Get;
                if (getMethodIdx >= 0 && getMethodIdx < metadata.MethodDefinitions.Length)
                {
                    var getMd = metadata.MethodDefinitions[getMethodIdx];
                    var propNS = string.Empty;
                    getterName = getMd.Name ?? $"get_{propName}";
                    propType = typeResolver != null
                        ? Common.TypeUtils.CleanTypeName(typeResolver.ResolveTypeName(getMd.ReturnTypeIndex), out propNS)
                        : "object";
                    getterAccess = Pipeline.Stages.ClassHeaderBuilder.GetMethodAccess(getMd.Flags);
                    isStatic = (getMd.Flags & 0x0010) != 0;
                    AddUsings(parent, propNS);
                }
            }

            if (hasSet)
            {
                int setMethodIdx = td.MethodStart + prop.Set;
                if (setMethodIdx >= 0 && setMethodIdx < metadata.MethodDefinitions.Length)
                {
                    var setMd = metadata.MethodDefinitions[setMethodIdx];
                    setterName = setMd.Name ?? $"set_{propName}";
                    setterAccess = Pipeline.Stages.ClassHeaderBuilder.GetMethodAccess(setMd.Flags);
                    if (!hasGet && setMd.ParameterCount > 0)
                    {
                        int paramIdx = setMd.ParameterStart;
                        if (paramIdx >= 0 && paramIdx < metadata.ParameterDefinitions.Length && typeResolver != null)
                            {
                                propType = Common.TypeUtils.CleanTypeName(typeResolver.ResolveTypeName(metadata.ParameterDefinitions[paramIdx].TypeIndex), out string propNS);
                                AddUsings(parent, propNS);
                            }
                    }
                }
            }

            string backingField = $"<{propName}>k__BackingField";
            bool isAuto = fieldNameSet.Contains(backingField);

            asset.Properties.Add(new ScriptAsset.PropertyInfo
            {
                Name = propName,
                TypeName = propType,
                HasGetter = hasGet,
                HasSetter = hasSet,
                GetterMethodName = getterName,
                SetterMethodName = setterName,
                GetterAccess = getterAccess,
                SetterAccess = setterAccess,
                IsStatic = isStatic,
                IsAutoProperty = isAuto,
                BackingFieldName = isAuto ? backingField : null,
                PropDef = prop,
            });

            if (getterName != null) asset.SkipMethodNames.Add(getterName);
            if (setterName != null) asset.SkipMethodNames.Add(setterName);
        }
    }

    // ─── Events ──────────────────────────────────────────────────────────────

    private static void PopulateEvents(ScriptAsset asset, TypeDefinition td, MetadataParser metadata, TypeResolver? typeResolver, ScriptAsset parent)
    {
        for (int e = 0; e < td.EventCount; e++)
        {
            int eventIdx = td.EventStart + e;
            if (eventIdx < 0 || eventIdx >= metadata.EventDefinitions.Length) continue;
            var evt = metadata.EventDefinitions[eventIdx];

            string handlerNS = string.Empty;
            string handlerType = typeResolver != null
                ? Common.TypeUtils.CleanTypeName(typeResolver.ResolveTypeName(evt.TypeIndex), out handlerNS)
                : "EventHandler";

            AddUsings(parent, handlerNS);
            string? addName = null, removeName = null, raiseName = null;
            if (evt.Add >= 0)
            {
                int addIdx = td.MethodStart + evt.Add;
                if (addIdx >= 0 && addIdx < metadata.MethodDefinitions.Length)
                    addName = metadata.MethodDefinitions[addIdx].Name;
            }
            if (evt.Remove >= 0)
            {
                int removeIdx = td.MethodStart + evt.Remove;
                if (removeIdx >= 0 && removeIdx < metadata.MethodDefinitions.Length)
                    removeName = metadata.MethodDefinitions[removeIdx].Name;
            }
            if (evt.Raise >= 0)
            {
                int raiseIdx = td.MethodStart + evt.Raise;
                if (raiseIdx >= 0 && raiseIdx < metadata.MethodDefinitions.Length)
                    raiseName = metadata.MethodDefinitions[raiseIdx].Name;
            }

            asset.Events.Add(new ScriptAsset.EventInfo
            {
                Name = evt.Name ?? $"Event_{e}",
                HandlerTypeName = handlerType,
                AddMethodName = addName,
                RemoveMethodName = removeName,
                RaiseMethodName = raiseName,
                EvtDef = evt,
            });

            if (addName != null) asset.SkipMethodNames.Add(addName);
            if (removeName != null) asset.SkipMethodNames.Add(removeName);
            if (raiseName != null) asset.SkipMethodNames.Add(raiseName);
        }
    }

    // ─── Methods ─────────────────────────────────────────────────────────────

    private static void PopulateMethods(ScriptAsset asset, TypeDefinition td, MetadataParser metadata, TypeResolver? typeResolver, ScriptAsset parent)
    {
        for (int m = 0; m < td.MethodCount; m++)
        {
            int methIdx = td.MethodStart + m;
            if (methIdx < 0 || methIdx >= metadata.MethodDefinitions.Length) continue;
            var md = metadata.MethodDefinitions[methIdx];

            string retNS = string.Empty;
            string retType = typeResolver != null
                ? Common.TypeUtils.CleanTypeName(typeResolver.ResolveTypeName(md.ReturnTypeIndex), out retNS)
                : "void";

            AddUsings(parent, retNS);    
            var parameters = new List<ScriptAsset.ParameterInfo>();
            var paramNS = string.Empty;
            for (int pp = 0; pp < md.ParameterCount; pp++)
            {
                int pi = md.ParameterStart + pp;
                if (pi >= 0 && pi < metadata.ParameterDefinitions.Length)
                {
                    var pd = metadata.ParameterDefinitions[pi];
                    parameters.Add(new ScriptAsset.ParameterInfo
                    {
                        Name = pd.Name ?? $"p{pp}",
                        TypeName = typeResolver != null? Common.TypeUtils.CleanTypeName(typeResolver.ResolveTypeName(pd.TypeIndex), out paramNS): "object"
                    });

                    AddUsings(parent, paramNS);
                }
            }

            asset.Methods.Add(new ScriptAsset.MethodInfo
            {
                GlobalIndex = methIdx,
                Name = md.Name ?? $"Method_{methIdx}",
                ReturnType = retType,
                Flags = md.Flags,
                ParameterCount = md.ParameterCount,
                Parameters = parameters,
                MethodDef = md,
            });
        }
    }

    // ─── Custom Attributes ───────────────────────────────────────────────────

    /// <summary>
    /// Populate pre-formatted C# attribute strings on the script and all its members.
    /// Must be called AFTER Build(). Requires the image that owns this type for
    /// token-scoped lookup.
    /// </summary>
    public static void PopulateAttributes(ScriptAsset asset, CustomAttributeResolver resolver, ImageDefinition image)
    {
        var td = asset.TypeDef;

        // ── Pseudo-custom attributes (ECMA-335 flags, NOT in attribute blob) ──
        //
        // [Serializable] → TypeAttributes.Serializable = 0x00002000
        // These are stored as flag bits on the TypeDefinition, not as entries
        // in the AttributeData blob. Must be synthesized here.
        if ((td.Flags & 0x2000) != 0)
            asset.Attributes.Add("[Serializable]");

        // Type-level attributes from blob (token table 0x02 = TypeDef)
        asset.Attributes.AddRange(resolver.Resolve(td.Token, image));

        // Field attributes (token table 0x04 = Field)
        foreach (var field in asset.Fields)
        {
            // [NonSerialized] → FieldAttributes.NotSerialized = 0x0080
            // Pseudo-custom attribute stored as a flag bit, not in attribute blob
            if (field.FieldDef.IsNotSerialized)
                field.Attributes.Add("[NonSerialized]");

            field.Attributes.AddRange(resolver.Resolve(field.FieldDef.Token, image));
        }

        // Property attributes (token table 0x17 = Property)
        foreach (var prop in asset.Properties)
        {
            if (prop.PropDef != null)
                prop.Attributes.AddRange(resolver.Resolve(prop.PropDef.Token, image));
        }

        // Event attributes (token table 0x0E = Event)
        foreach (var evt in asset.Events)
        {
            if (evt.EvtDef != null)
                evt.Attributes.AddRange(resolver.Resolve(evt.EvtDef.Token, image));
        }

        // Method attributes (token table 0x06 = Method)
        foreach (var method in asset.Methods)
            method.Attributes.AddRange(resolver.Resolve(method.MethodDef.Token, image));

        // Nested types
        foreach (var nested in asset.NestedTypes)
            PopulateAttributes(nested, resolver, image);
    }

    private static void AddUsings(ScriptAsset asset, string nameSpace)
    {
        if(!string.IsNullOrEmpty(nameSpace))
        {
            if (!asset.Usings.Contains(nameSpace))
                asset.Usings.Add(nameSpace);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static bool IsImplicitBase(string baseName, TypeDefinition td)
    {
        return baseName is "System.Object" or "object" or "System.ValueType" or "System.Enum"
            || (td.IsValueType && baseName == "System.ValueType")
            || (td.IsEnum && baseName == "System.Enum");
    }
}
