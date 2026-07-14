// VTableLayout — maps vtable slot indices to resolved method names.
// Used to resolve virtual calls like [x9]() where x9 is loaded from vtable.

using System.Collections.Generic;

namespace Rosetta.Model;

/// <summary>
/// Represents the virtual method table for a type.
/// Maps slot index → method name for virtual dispatch resolution.
/// </summary>
public sealed class VTableLayout
{
    /// <summary>Fully qualified name of the owning type.</summary>
    public string TypeName { get; }

    /// <summary>Slot index → method entry.</summary>
    private readonly Dictionary<int, VTableEntry> _slots = new();

    public VTableLayout(string typeName)
    {
        TypeName = typeName;
    }

    public void AddSlot(int slotIndex, VTableEntry entry)
    {
        _slots[slotIndex] = entry;
    }

    public void CopyFrom(VTableLayout other)
    {
        foreach (var kvp in other._slots)
        {
            _slots[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// Get the method name at a given vtable slot.
    /// Returns null if slot is empty or unknown.
    /// </summary>
    public string? GetMethodAtSlot(int slotIndex)
    {
        return _slots.TryGetValue(slotIndex, out var entry) ? entry.MethodName : null;
    }

    /// <summary>
    /// Get full method info at a given vtable slot.
    /// </summary>
    public VTableEntry? GetEntryAtSlot(int slotIndex)
    {
        return _slots.TryGetValue(slotIndex, out var entry) ? entry : null;
    }

    /// <summary>Number of populated vtable slots.</summary>
    public int SlotCount => _slots.Count;

    public sealed class VTableEntry
    {
        /// <summary>Full method name (e.g., "System.Object::ToString").</summary>
        public required string MethodName { get; init; }

        /// <summary>Global method definition index (for signature lookup).</summary>
        public required int MethodIndex { get; init; }

        public override string ToString() => MethodName;
    }
}
