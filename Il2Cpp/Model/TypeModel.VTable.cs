using System;
using Rosetta.Pipeline;

namespace Rosetta.Model;

public sealed partial class TypeModel
{
    public VTableLayout.VTableEntry? ResolveVTableMethod(string typeName, int slotIndex)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"TypeModel: ResolveVTableMethod called for type={typeName}, slot={slotIndex}");
        // First try the type directly (works for ALL types including mscorlib like System.Object)
        if (_vtableByType.TryGetValue(typeName, out var directLayout))
        {
            var entry = directLayout.GetEntryAtSlot(slotIndex);
            if (entry != null) 
            {
                return entry;
            }
        }

        // If not found (or if it's a derived class without an override), walk the inheritance chain
        if (!FieldLayoutsByTypeName.TryGetValue(typeName, out int typeDefIndex))
        {
            return null;
        }

        while (typeDefIndex >= 0 && typeDefIndex < _metadata.TypeDefinitions.Length)
        {
            var typeDef = _metadata.TypeDefinitions[typeDefIndex];
            
            if (_vtableByType.TryGetValue(typeDef.FullName, out var layout))
            {
                var entry = layout.GetEntryAtSlot(slotIndex);
                if (entry != null) 
                {
                    return entry;
                }
            }

            if (typeDef.ParentIndex < 0) break;
            typeDefIndex = ResolveTypeDefIndexFromTypeIndex(typeDef.ParentIndex);
        }

        if (ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"TypeModel: ResolveVTableMethod failed to resolve slot={slotIndex} for type={typeName}");
        }
        return null;
    }

    public VTableLayout? GetVTableLayout(string typeName)
    {
        _vtableByType.TryGetValue(typeName, out var layout);
        return layout;
    }

    private void BuildVTableLayouts()
    {
        for (int methodIdx = 0; methodIdx < _metadata.MethodDefinitions.Length; methodIdx++)
        {
            var method = _metadata.MethodDefinitions[methodIdx];
            if (method.Slot == 0xFFFF) continue; // no vtable slot
            if (method.Name == null) continue;
            if (method.DeclaringTypeIndex < 0 || method.DeclaringTypeIndex >= _metadata.TypeDefinitions.Length) continue;

            var typeDef = _metadata.TypeDefinitions[method.DeclaringTypeIndex];
            string typeName = typeDef.FullName;

            if (!_vtableByType.TryGetValue(typeName, out var layout))
            {
                layout = new VTableLayout(typeName);
                _vtableByType[typeName] = layout;
            }

            string methodFullName = $"{typeName}::{method.Name}";
            layout.AddSlot(method.Slot, new VTableLayout.VTableEntry
            {
                MethodName = methodFullName,
                MethodIndex = methodIdx
            });
        }
    }
}
