using System;
using System.Collections.Generic;
using Rosetta.Common;
using Rosetta.Metadata;
using Rosetta.Pipeline;

namespace Rosetta.Model;

public sealed partial class TypeModel
{
    public string? ResolveEnumLiteralByTypeDefIndex(int typeDefIndex, long value)
    {
        if (!_enumInfoByTypeDefIndex.TryGetValue(typeDefIndex, out var enumInfo))
        {
            if (ConsoleReporter.Verbose)
            {
                ConsoleReporter.Debug($"TypeModel: ResolveEnumLiteralByTypeDefIndex: enum info not found for typeDefIndex={typeDefIndex}");
            }
            return null;
        }
        var result = enumInfo.Resolve(value);
        if (result == null && ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"TypeModel: ResolveEnumLiteralByTypeDefIndex: failed to resolve literal value={value} for enum typeDefIndex={typeDefIndex}");
        }
        return result;
    }

    private void BuildEnumInfo()
    {
        if (_metadata.FieldDefaultValues.Length == 0)
            return;

        var defaultLookup = new Dictionary<int, FieldDefaultValueDef>();
        foreach (var fdv in _metadata.FieldDefaultValues)
            defaultLookup[fdv.FieldIndex] = fdv;

        var attributeResolver = _metadata.AttributeData.Length > 0
            ? new CustomAttributeResolver(_metadata, _typeResolver)
            : null;

        for (int typeIdx = 0; typeIdx < _metadata.TypeDefinitions.Length; typeIdx++)
        {
            var td = _metadata.TypeDefinitions[typeIdx];
            if (!td.IsEnum) continue;

            var entries = new List<EnumValueEntry>();
            for (int f = 0; f < td.FieldCount; f++)
            {
                int fieldIdx = td.FieldStart + f;
                if (fieldIdx < 0 || fieldIdx >= _metadata.FieldDefinitions.Length) continue;

                var field = _metadata.FieldDefinitions[fieldIdx];
                if (field.Name == "value__") continue;
                if (!defaultLookup.TryGetValue(fieldIdx, out var fdv)) continue;
                if (!TryReadEnumConstant(fdv, out long value)) continue;

                entries.Add(new EnumValueEntry(field.Name ?? $"Field_{f}", value));
            }

            if (entries.Count > 0)
            {
                bool hasFlagsAttribute = false;
                var image = FindImageForTypeDefIndex(typeIdx);
                if (attributeResolver != null && image != null)
                    hasFlagsAttribute = attributeResolver.HasAttribute(td.Token, image, "System.FlagsAttribute");

                _enumInfoByTypeDefIndex[typeIdx] = new EnumInfo(td.FullName, entries, hasFlagsAttribute);
            }
        }
    }

    private ImageDefinition? FindImageForTypeDefIndex(int typeDefIndex)
    {
        foreach (var image in _metadata.ImageDefinitions)
        {
            int start = image.TypeStart;
            int end = start + (int)image.TypeCount;
            if (typeDefIndex >= start && typeDefIndex < end)
                return image;
        }
        return null;
    }

    private bool TryReadEnumConstant(FieldDefaultValueDef fdv, out long value)
    {
        value = 0;
        if (fdv.DataIndex < 0 || fdv.DataIndex >= _metadata.DefaultValuesData.Length)
            return false;

        bool isSigned = true;
        if (fdv.TypeIndex >= 0)
        {
            var typeInfo = _typeResolver.GetTypeByIndex(fdv.TypeIndex);
            if (typeInfo.HasValue)
            {
                isSigned = typeInfo.Value.TypeEnum is
                    Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I1 or
                    Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I2 or
                    Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I4 or
                    Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I8;
            }
        }

        int offset = fdv.DataIndex;
        if (MetadataDecoder.TryReadCompressedUInt32(_metadata.DefaultValuesData, ref offset, out uint raw))
        {
            value = MetadataDecoder.DecodeZigZag(raw, isSigned);
            return true;
        }
        return false;
    }
}
