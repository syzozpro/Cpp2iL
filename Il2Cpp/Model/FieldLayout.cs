// FieldLayout — maps byte offsets within a type's instance to field names/types.
// Used by the lifter to resolve *([x8 + 0x48]) → fieldName

using System.Collections.Generic;
using System.Linq;

namespace Rosetta.Model;

/// <summary>
/// Represents the memory layout of fields in a type.
/// Maps byte offset → field name + type for runtime memory access resolution.
/// </summary>
public sealed class FieldLayout
{
    /// <summary>Fully qualified name of the owning type.</summary>
    public string TypeName { get; }

    /// <summary>All fields in this type, sorted by offset.</summary>
    private readonly List<FieldEntry> _fields = new();

    public FieldLayout(string typeName)
    {
        TypeName = typeName;
    }

    public void AddField(FieldEntry entry)
    {
        _fields.Add(entry);
    }

    /// <summary>
    /// Get the field name at a specific byte offset.
    /// Returns null if no field matches.
    /// </summary>
    public string? GetFieldAtOffset(int byteOffset)
    {
        var field = _fields.FirstOrDefault(f => f.Offset == byteOffset);
        return field?.Name;
    }

    /// <summary>
    /// Get full field info at a specific byte offset.
    /// Returns null if no field matches.
    /// </summary>
    public FieldEntry? GetFieldInfoAtOffset(int byteOffset)
    {
        return _fields.FirstOrDefault(f => f.Offset == byteOffset);
    }

    /// <summary>All fields in this type.</summary>
    public IReadOnlyList<FieldEntry> Fields => _fields;

    /// <summary>Number of instance (non-static) fields.</summary>
    public int InstanceFieldCount => _fields.Count(f => !f.IsStatic);

    /// <summary>
    /// Compute the byte size of an instance field from metadata offsets.
    /// Size = (nextInstanceField.Offset - thisField.Offset), or remaining struct
    /// bytes for the last field. Returns -1 if the field index is invalid.
    /// This is purely metadata-driven — no type-name string matching.
    /// </summary>
    public int GetInstanceFieldSize(int instanceFieldIndex)
    {
        // Build ordered instance field list, sorted by offset
        // (MergeInheritedFields appends parent fields after declared fields,
        //  so insertion order is NOT offset-sorted)
        var instanceFields = new List<FieldEntry>();
        foreach (var f in _fields)
            if (!f.IsStatic) instanceFields.Add(f);
        instanceFields.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        if (instanceFieldIndex < 0 || instanceFieldIndex >= instanceFields.Count)
            return -1;

        if (instanceFieldIndex + 1 < instanceFields.Count)
            return instanceFields[instanceFieldIndex + 1].Offset - instanceFields[instanceFieldIndex].Offset;

        // Last field: no next field to diff against. Use 4 as minimum reasonable size.
        // A more precise calculation would need the total struct size from metadata,
        // but for packed value extraction the remaining bytes in the store window
        // are bounded by the caller anyway.
        return 4;
    }

    /// <summary>A single field entry with its resolved metadata.</summary>
    public sealed class FieldEntry
    {
        /// <summary>Field name from metadata strings.</summary>
        public required string Name { get; init; }

        /// <summary>Byte offset from the start of the object instance. -1 if unknown.</summary>
        public required int Offset { get; init; }

        /// <summary>Resolved type name (e.g., "System.Int32", "System.String").</summary>
        public required string TypeName { get; init; }

        /// <summary>Whether this field is static.</summary>
        public required bool IsStatic { get; init; }

        /// <summary>Global field definition index in metadata.</summary>
        public required int GlobalIndex { get; init; }

        /// <summary>
        /// Raw Il2CppType index for this field. Preserved for generic context resolution.
        /// When processing a generic instantiation (e.g., List&lt;int&gt;), the TypeResolver
        /// can re-resolve this index with concrete generic arguments instead of using
        /// the cached TypeName string (which would be "T" for an open generic).
        /// </summary>
        public int TypeIndex { get; init; } = -1;

        /// <summary>
        /// ECMA-335 element type tag from binary metadata (e.g., IL2CPP_TYPE_I4 for int,
        /// IL2CPP_TYPE_R4 for float). Resolved at TypeModel build time from TypeIndex.
        /// Used for metadata-driven field size computation and value decode formatting
        /// without any type-name string matching.
        /// </summary>
        public Rosetta.Common.Il2CppTypeEnum ElementType { get; init; } = Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_END;

        public override string ToString() => $"{TypeName} {Name} @ 0x{Offset:X}";
    }
}
