using System;
using System.Collections.Generic;
using Rosetta.Metadata;
using Rosetta.Model;

namespace Rosetta.Pipeline.Stages;

/// <summary>
/// Handles type linking, recursive nested type resolution, and BaseAsset linkage.
/// Extracted from AssemblyPipeline for single-responsibility.
/// </summary>
public static class ScriptLinker
{
    public static void RegisterAndLink(ScriptAsset script, Dictionary<string, ScriptAsset> dict, List<ScriptAsset> pending)
    {
        // Register this script + nested types
        RegisterScript(script, dict);

        // Try to resolve this script's base + nested
        LinkBasesRecursive(script, dict, pending);

        // Drain pending: any previously-unresolved script might now find its base
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var p = pending[i];
            p.BaseAsset = Resolve(p.BaseTypeName!, dict);
            if (p.BaseAsset != null)
                pending.RemoveAt(i);
        }
    }

    private static void LinkBasesRecursive(ScriptAsset script, Dictionary<string, ScriptAsset> dict, List<ScriptAsset> pending)
    {
        if (script.BaseTypeName != null)
        {
            script.BaseAsset = Resolve(script.BaseTypeName, dict);
            if (script.BaseAsset == null)
                pending.Add(script);
        }

        foreach (var nested in script.NestedTypes)
            LinkBasesRecursive(nested, dict, pending);
    }

    public static void RegisterScript(ScriptAsset script, Dictionary<string, ScriptAsset> dict)
    {
        dict.TryAdd(script.FullName, script);
        if (script.Name != script.FullName)
            dict.TryAdd(script.Name, script);

        // Partial namespace forms: "UnityEngine.UI.Graphic" → "UI.Graphic" → "Graphic"
        var fn = script.FullName;
        int dot;
        while ((dot = fn.IndexOf('.')) >= 0)
        {
            fn = fn[(dot + 1)..];
            if (fn.Length > 0) dict.TryAdd(fn, script);
        }

        foreach (var nested in script.NestedTypes)
            RegisterScript(nested, dict);
    }

    public static ScriptAsset? Resolve(string typeName, Dictionary<string, ScriptAsset> dict)
    {
        if (dict.TryGetValue(typeName, out var found))
            return found;

        // Generic: "BaseCell<T>" → "BaseCell" → "BaseCell`1"
        int angle = typeName.IndexOf('<');
        if (angle > 0)
        {
            var baseName = typeName[..angle];
            if (dict.TryGetValue(baseName, out found))
                return found;
            int argc = 1;
            for (int i = angle; i < typeName.Length; i++)
                if (typeName[i] == ',') argc++;
            if (dict.TryGetValue($"{baseName}`{argc}", out found))
                return found;
        }
        return null;
    }

    public static void PopulateNestedTypes(ScriptAsset parent, TypeDefinition parentTd, Il2CppContext context, bool onlyStructs = false)
    {
        if (parentTd.NestedTypeCount <= 0 || context.Metadata == null) return;

        for (int n = 0; n < parentTd.NestedTypeCount; n++)
        {
            int nestedSlot = parentTd.NestedTypesStart + n;
            if (nestedSlot < 0 || nestedSlot >= context.Metadata.NestedTypes.Length) continue;

            int nestedIdx = context.Metadata.NestedTypes[nestedSlot];
            if (nestedIdx < 0 || nestedIdx >= context.Metadata.TypeDefinitions.Length) continue;

            var nestedTd = context.Metadata.TypeDefinitions[nestedIdx];
            if (onlyStructs && !nestedTd.IsStruct) continue;

            var nestedScript = ScriptAssetBuilder.Build(nestedIdx, nestedTd, context.Metadata, context.TypeResolver, parent);

            // Recurse into sub-nested types
            PopulateNestedTypes(nestedScript, nestedTd, context, onlyStructs);

            parent.NestedTypes.Add(nestedScript);
        }
    }
}
