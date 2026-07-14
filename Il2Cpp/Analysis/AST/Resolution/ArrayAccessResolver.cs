using Rosetta.Common;

namespace Rosetta.Analysis.AST;

/// <summary>IL2CPP memory offset → structured array access conversion.</summary>
public sealed partial class ExprPropagator
{
    /// <summary>Try to convert a memory dereference at an IL2CPP array offset to a
    /// structured access: 0x18→.Length, 0x20+→[index], 0x20+→[index].field for struct arrays.
    /// <param name="accessBitWidth">Bit width of the store/load. When narrower than the element
    /// size, indicates a sub-element field access that needs FieldLayout resolution.</param></summary>
    private ExprNode? TryMakeArrayAccess(ExprNode baseExpr, long offset, int accessBitWidth = 0)
    {
        if (offset == Constants.ArrayDataOffset && baseExpr is ExprBinary bin && bin.Op == "+")
            return new ExprIndex(bin.Left, (bin.Right as ExprBinary)?.Left ?? bin.Right);

        // Verify that baseExpr is actually an array before applying array heuristics
        string? arrayType = baseExpr.InferredType;
        if (arrayType == null && baseExpr is ExprNew ne)
            arrayType = ne.TypeName + "[]";

        bool isArray = Rosetta.Analysis.Utils.TypeAliasUtils.ResolvedType.Parse(arrayType ?? "").IsArray;

        if (!isArray) return null;

        // IL2CPP managed array layout (1D):
        //   0x18 = Length (int32)
        //   0x20 = element[0]
        //   0x20 + elemSize*1 = element[1] ...
        if (offset == Constants.ArrayLengthOffset)
            return new ExprField(baseExpr, "Length");

        if (offset < Constants.ArrayDataOffset) return null;

        int elemSize = GetElementSizeForExpr(baseExpr);
        if (elemSize <= 0) return null;

        long dataOffset = offset - Constants.ArrayDataOffset;
        int elementIndex = (int)(dataOffset / elemSize);
        long fieldByteOffset = dataOffset % elemSize;

        var indexExpr = new ExprIndex(baseExpr, new ExprLiteral(elementIndex))
        {
            InferredType = Common.TypeUtils.GetArrayElementType(arrayType)
        };

        // Check if this is a sub-element field access:
        // - The offset doesn't align to an element boundary (fieldByteOffset != 0), OR
        // - The access width is narrower than the element size
        bool isSubElement = fieldByteOffset != 0 || (accessBitWidth > 0 && accessBitWidth / 8 < elemSize);

        if (!isSubElement)
            return indexExpr;

        // Sub-element field access: resolve field name from FieldLayout metadata
        var resolved = TryResolveStructField(baseExpr, indexExpr, fieldByteOffset);
        if (resolved != null)
            return resolved;

        // Couldn't resolve field — return plain index if aligned, null otherwise
        return fieldByteOffset == 0 ? indexExpr : null;
    }

    /// <summary>Resolve a sub-element field within a struct array element using FieldLayout metadata.</summary>
    private ExprNode? TryResolveStructField(ExprNode baseExpr, ExprIndex indexExpr, long fieldByteOffset)
    {
        if (_typeModel == null) return null;

        // Extract the element type name from the array type
        string? arrayType = baseExpr.InferredType;
        if (arrayType == null && baseExpr is ExprNew ne)
            arrayType = ne.TypeName + "[]";
        if (arrayType == null || !arrayType.Contains('[')) return null;

        int bracket = arrayType.IndexOf('[');
        string elementTypeName = arrayType[..bracket];

        // Look up FieldLayout for the element type — O(1)
        if (!_typeModel.FieldLayoutsByTypeName.TryGetValue(elementTypeName, out int typeDefIndex))
            return null;
        if (!_typeModel.FieldLayouts.TryGetValue(typeDefIndex, out var layout))
            return null;

        // IL2CPP object header (16 bytes on 64-bit). Instance field metadata offsets
        // include this header, but array element data doesn't.
        const int ObjectHeaderSize = 16;
        int metadataOffset = (int)fieldByteOffset + ObjectHeaderSize;

        var field = layout.GetFieldInfoAtOffset(metadataOffset);
        if (field == null) return null;

        return new ExprField(indexExpr, field.Name);
    }

    /// <summary>
    /// Try to resolve a multi-dimensional array element access from a complex
    /// address expression and a final offset.
    ///
    /// Pattern: *(arr + 0x20 + (dim1_stride << K) + C) = value
    /// where K = row * elem_size_log2  and  C = col * elem_size
    ///
    /// IL2CPP multi-dim array layout:
    ///   [arr + 0x10] = bounds pointer
    ///   [bounds + 0x00] = dim0 length
    ///   [bounds + 0x10] = dim1 length
    ///   [arr + 0x20] = data start (elements in row-major order)
    ///   Element [r, c] at data_start + (r * dim1_len + c) * elem_size
    ///
    /// The compiler optimizes this to:
    ///   ADD Xtemp, Xarr, #0x20          ; data_start
    ///   ADD Xaddr, Xtemp, Xdim1, LSL #K ; + dim1_len * (row * elem_size)
    ///   STR Wval, [Xaddr, #C]           ; + col * elem_size
    /// </summary>
    private ExprNode? TryMakeMultiDimAccess(ExprNode baseExpr, long finalOffset)
    {
        // Walk the ADD chain to find: arr_base, data_start_offset, shift_expr, shift_amount
        if (baseExpr is not ExprBinary topAdd || topAdd.Op != "+")
            return null;

        // Try both orderings: (arr+0x20) + (x<<K) or (x<<K) + (arr+0x20)
        ExprNode? arrayBase;
        int shiftAmount;
        bool isFolded = false;

        if (TryExtractMultiDimComponents(topAdd.Left, topAdd.Right, out arrayBase, out shiftAmount, out isFolded) ||
            TryExtractMultiDimComponents(topAdd.Right, topAdd.Left, out arrayBase, out shiftAmount, out isFolded))
        {
            if (!TypeUtils.IsMultiDimArray(arrayBase?.InferredType))
                return null;

            int elemSize = ResolveElementSize(arrayBase!.InferredType);
            if (elemSize <= 0) return null;

            long adjustedFinalOffset = finalOffset;
            if (isFolded)
            {
                if (finalOffset < Constants.ArrayDataOffset) return null;
                adjustedFinalOffset = finalOffset - Constants.ArrayDataOffset;
            }

            // shiftAmount = row * elem_size (the LSL amount in bytes)
            // finalOffset = col * elem_size
            int rowTimesElemSize = 1 << shiftAmount; // 2^K
            if (rowTimesElemSize % elemSize != 0) return null;
            int row = rowTimesElemSize / elemSize;

            if (adjustedFinalOffset % elemSize != 0) return null;
            int col = (int)(adjustedFinalOffset / elemSize);

            return new ExprIndex(arrayBase, new ExprVar($"{row}, {col}"));
        }

        return null;
    }

    /// <summary>Try to extract the multi-dim components from a pair of expressions.</summary>
    private static bool TryExtractMultiDimComponents(
        ExprNode dataStartCandidate, ExprNode shiftCandidate,
        out ExprNode? arrayBase, out int shiftAmount, out bool isFolded)
    {
        arrayBase = null;
        shiftAmount = 0;
        isFolded = false;

        // shiftCandidate should be: ExprBinary("<<", var, literal)
        if (shiftCandidate is not ExprBinary shiftOp || shiftOp.Op != "<<")
            return false;
        if (shiftOp.Right is not ExprLiteral shiftLit)
            return false;
        int shiftVal;
        if (shiftLit.Value is int i)
            shiftVal = i;
        else if (shiftLit.Value is long l)
            shiftVal = (int)l;
        else
            return false;

        // dataStartCandidate should be: ExprBinary("+", arr, 0x20)
        if (dataStartCandidate is ExprBinary addOp && addOp.Op == "+")
        {
            long? offsetVal = ExtractLiteralLong(addOp.Right);
            if (offsetVal == Constants.ArrayDataOffset)
            {
                arrayBase = addOp.Left;
                shiftAmount = shiftVal;
                return true;
            }
            // Check reversed: (0x20 + arr)
            offsetVal = ExtractLiteralLong(addOp.Left);
            if (offsetVal == Constants.ArrayDataOffset)
            {
                arrayBase = addOp.Right;
                shiftAmount = shiftVal;
                return true;
            }
        }

        // Folded pattern: dataStartCandidate is just arr (folding +0x20 into finalOffset)
        arrayBase = dataStartCandidate;
        shiftAmount = shiftVal;
        isFolded = true;
        return true;
    }

    /// <summary>Extract a long value from an ExprLiteral, handling both int and long.</summary>
    private static long? ExtractLiteralLong(ExprNode node)
    {
        if (node is ExprLiteral lit)
        {
            if (lit.Value is int i) return i;
            if (lit.Value is long l) return l;
        }
        return null;
    }

    /// <summary>Derive array element size from InferredType or ExprNew.TypeName.</summary>
    private int GetElementSizeForExpr(ExprNode baseExpr)
    {
        string? arrayType = baseExpr.InferredType;
        if (arrayType == null && baseExpr is ExprNew ne)
            arrayType = ne.TypeName + "[]";

        return ResolveElementSize(arrayType);
    }

    private int ResolveElementSize(string? arrayType)
    {
        if (arrayType == null) return 8;

        int bracket = arrayType.LastIndexOf('[');
        string elementTypeName = bracket >= 0 ? arrayType[..bracket] : arrayType;

        // Try metadata size first
        if (_typeModel != null && _typeModel.TryGetMetadataSize(elementTypeName, out int size, out _))
        {
            if (size != 8)
                return size;
        }

        string cleanName = TypeUtils.ToAlias(elementTypeName);

        int primSize = TypeUtils.GetPrimitiveSize(cleanName);
        if (primSize > 0) return primSize;

        // Well-known Unity struct invariants
        switch (cleanName)
        {
            case "Vector2": return 8;
            case "Vector3": return 12;
            case "Vector4":
            case "Quaternion":
            case "Color": return 16;
        }

        return 8; // fallback
    }
}
