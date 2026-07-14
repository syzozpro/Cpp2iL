using Rosetta.Binary;
using Rosetta.Common;
using Rosetta.Metadata;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.Resolve;

/// <summary>
/// Resolves FieldRef metadata indices to their raw initialization data
/// from DefaultValuesData (Section 8) in global-metadata.dat.
///
/// Used to recover array literal initializers from IL2CPP's
/// RuntimeHelpers.InitializeArray(array, fieldHandle) pattern.
///
/// Resolution path:
///   FieldRef[i].TypeIndex → Il2CppType → TypeDef → FieldStart
///   FieldStart + FieldRef[i].FieldIndex → global FieldDef index
///   FieldDefaultValues[j].FieldIndex == globalFdIdx → DataIndex
///   DefaultValuesData[DataIndex..] → raw bytes
/// </summary>
public sealed class FieldRvaResolver
{
    private readonly MetadataParser _metadata;
    private readonly TypeResolver _typeResolver;

    // Pre-built lookup: global FieldDef index → FDV entry index
    private readonly Dictionary<int, int> _fdvByFieldDef = new();

    // Reverse lookup: field label (e.g. "<PrivateImplementationDetails>.97CA15...")
    // → FieldRef index, for resolving annotations back to data
    private readonly Dictionary<string, int> _fieldRefByLabel = new();

    public FieldRvaResolver(MetadataParser metadata, TypeResolver typeResolver)
    {
        _metadata = metadata;
        _typeResolver = typeResolver;

        // Build reverse index: FieldDef index → FDV entry
        for (int i = 0; i < metadata.FieldDefaultValues.Length; i++)
        {
            var fdv = metadata.FieldDefaultValues[i];
            _fdvByFieldDef[fdv.FieldIndex] = i;
        }

        // Build reverse label → FieldRef index map
        for (int i = 0; i < metadata.FieldRefs.Length; i++)
        {
            var fr = metadata.FieldRefs[i];
            var il2cppType = typeResolver.GetTypeByIndex(fr.TypeIndex);
            if (!il2cppType.HasValue) continue;

            if (il2cppType.Value.TypeEnum != Il2CppTypeEnum.IL2CPP_TYPE_CLASS &&
                il2cppType.Value.TypeEnum != Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
                continue;

            int typeDefIdx = il2cppType.Value.KlassIndex;
            if (typeDefIdx < 0 || typeDefIdx >= metadata.TypeDefinitions.Length) continue;

            var td = metadata.TypeDefinitions[typeDefIdx];
            int fieldStart = td.FieldStart;
            if (fieldStart < 0) continue;

            int globalFdIdx = fieldStart + fr.FieldIndex;
            if (globalFdIdx < 0 || globalFdIdx >= metadata.FieldDefinitions.Length) continue;

            var fd = metadata.FieldDefinitions[globalFdIdx];
            if (fd.Name != null)
            {
                string label = $"{td.FullName}.{fd.Name}";
                _fieldRefByLabel[label] = i;
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"FieldRvaResolver: {_fdvByFieldDef.Count} FDV entries, {_fieldRefByLabel.Count} field labels indexed");
    }

    /// <summary>
    /// Look up a FieldRef index from a field annotation label.
    /// The label is the text inside field(...) annotations,
    /// e.g. "&lt;PrivateImplementationDetails&gt;.97CA1592..."
    /// Returns -1 if not found.
    /// </summary>
    public int GetFieldRefIndex(string label)
    {
        return _fieldRefByLabel.GetValueOrDefault(label, -1);
    }

    /// <summary>
    /// Resolve a FieldRef index (from a metadata usage token with type=4)
    /// to the raw initialization bytes from DefaultValuesData.
    /// Returns null if the field has no default value data.
    /// </summary>
    public byte[]? ResolveFieldData(int fieldRefIndex, out int dataSize)
    {
        dataSize = 0;

        if (fieldRefIndex < 0 || fieldRefIndex >= _metadata.FieldRefs.Length)
            return null;

        var fr = _metadata.FieldRefs[fieldRefIndex];

        // Resolve FieldRef.TypeIndex → TypeDef → FieldStart
        var il2cppType = _typeResolver.GetTypeByIndex(fr.TypeIndex);
        if (!il2cppType.HasValue)
            return null;

        // Only CLASS and VALUETYPE have KlassIndex → TypeDef
        if (il2cppType.Value.TypeEnum != Il2CppTypeEnum.IL2CPP_TYPE_CLASS &&
            il2cppType.Value.TypeEnum != Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
            return null;

        int typeDefIdx = il2cppType.Value.KlassIndex;
        if (typeDefIdx < 0 || typeDefIdx >= _metadata.TypeDefinitions.Length)
            return null;

        var td = _metadata.TypeDefinitions[typeDefIdx];
        int fieldStart = td.FieldStart;
        if (fieldStart < 0)
            return null;

        int globalFieldDefIndex = fieldStart + fr.FieldIndex;
        if (globalFieldDefIndex < 0 || globalFieldDefIndex >= _metadata.FieldDefinitions.Length)
            return null;

        // Find the FieldDefaultValue entry for this FieldDef
        if (!_fdvByFieldDef.TryGetValue(globalFieldDefIndex, out int fdvIdx))
            return null;

        var fdv = _metadata.FieldDefaultValues[fdvIdx];
        if (fdv.DataIndex < 0 || fdv.DataIndex >= _metadata.DefaultValuesData.Length)
            return null;

        // Read all available bytes from DataIndex to end of blob.
        // The caller will use elementType + elementCount to determine how many to read.
        int available = _metadata.DefaultValuesData.Length - fdv.DataIndex;
        dataSize = available;

        byte[] result = new byte[dataSize];
        Array.Copy(_metadata.DefaultValuesData, fdv.DataIndex, result, 0, dataSize);
        return result;
    }

    /// <summary>
    /// Resolve a FieldRef index to typed array literal values.
    /// Returns a list of formatted C# literal strings, or null if unresolvable.
    /// </summary>
    /// <param name="fieldRefIndex">FieldRef index from the metadata usage token.</param>
    /// <param name="elementType">Element type name (e.g., "int", "float", "bool").</param>
    /// <param name="elementCount">Number of elements in the array.</param>
    public List<string>? ResolveArrayLiterals(int fieldRefIndex, string elementType, int elementCount)
    {
        var rawData = ResolveFieldData(fieldRefIndex, out int dataSize);
        if (rawData == null || rawData.Length == 0)
            return null;

        int elemSize = GetElementSize(elementType);
        if (elemSize <= 0)
            return null;

        // Verify we have enough data
        int needed = elementCount * elemSize;
        if (rawData.Length < needed)
            return null;

        var literals = new List<string>(elementCount);
        for (int i = 0; i < elementCount; i++)
        {
            int offset = i * elemSize;
            string literal = ReadElementLiteral(rawData, offset, elementType, elemSize);
            literals.Add(literal);
        }

        return literals;
    }

    /// <summary>Get the byte size of a C# primitive type.</summary>
    private static int GetElementSize(string elementType) => TypeUtils.GetPrimitiveSize(elementType);

    /// <summary>Read a single element from raw bytes and format as a C# literal.</summary>
    private static string ReadElementLiteral(byte[] data, int offset, string elementType, int size)
    {
        return elementType switch
        {
            "bool" => data[offset] != 0 ? "true" : "false",
            "byte" => data[offset].ToString(),
            "sbyte" => ((sbyte)data[offset]).ToString(),
            "short" => BitConverter.ToInt16(data, offset).ToString(),
            "ushort" => BitConverter.ToUInt16(data, offset).ToString(),
            "char" => $"'\\u{BitConverter.ToUInt16(data, offset):X4}'",
            "int" => BitConverter.ToInt32(data, offset).ToString(),
            "uint" => BitConverter.ToUInt32(data, offset).ToString() + "u",
            "float" => TypeUtils.FormatFloat(BitConverter.ToSingle(data, offset)),
            "long" => BitConverter.ToInt64(data, offset).ToString() + "L",
            "ulong" => BitConverter.ToUInt64(data, offset).ToString() + "UL",
            "double" => TypeUtils.FormatDouble(BitConverter.ToDouble(data, offset)),
            _ => $"0x{BitConverter.ToInt32(data, offset):X}",
        };
    }
}
