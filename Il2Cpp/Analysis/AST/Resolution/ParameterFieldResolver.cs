using System.Collections.Generic;
using Rosetta.Model;

namespace Rosetta.Analysis.AST;

/// <summary>Resolves memory accesses through typed parameters to metadata-backed field expressions.</summary>
public static class ParameterFieldResolver
{
    private const int ObjectHeaderSize = 16;

    public static ExprNode? TryResolve(ExprNode baseExpr, int regNum, long offset,
        IReadOnlyDictionary<int, string> gpParamTypeMap, TypeModel? typeModel)
    {
        if (typeModel == null)
            return null;

        if (!gpParamTypeMap.TryGetValue(regNum, out string? bareTypeName))
            return null;

        if (!typeModel.FieldLayoutsByTypeName.TryGetValue(bareTypeName, out int typeDefIndex))
            return null;

        int metadataOffset = (int)offset + ObjectHeaderSize;
        string? fieldName = typeModel.ResolveFieldAtOffset(typeDefIndex, metadataOffset);
        return fieldName == null ? null : new ExprField(baseExpr, fieldName);
    }
}
