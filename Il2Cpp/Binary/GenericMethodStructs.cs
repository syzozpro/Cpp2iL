// Source: WriteIl2CppGenericMethodTable.cs line 20
// Source: WriteIl2CppGenericMethodDefinitions.cs lines 47-53

namespace Rosetta.Binary;

/// <summary>
/// Il2CppGenericMethodFunctionsDefinitions — maps a MethodSpec index to
/// a pointer in the generic method pointer table.
///
/// Source Path: WriteIl2CppGenericMethodTable.cs line 20:
///   "{ " + m.TableIndex + ", " + m.PointerTableIndex + ", "
///        + invokerCollection.GetIndex(context, m.Method.GenericMethod) + ", "
///        + m.AdjustorThunkTableIndex + "}"
///
/// Binary layout: 4 × int32 = 16 bytes
/// </summary>
public readonly struct GenericMethodFuncDef
{
    /// <summary>Index into the MethodSpecs table (g_Il2CppMethodSpecTable).</summary>
    public int GenericMethodIndex { get; init; }

    /// <summary>Index into the genericMethodPointers table.</summary>
    public int PointerTableIndex { get; init; }

    /// <summary>Index into the invoker table.</summary>
    public int InvokerIndex { get; init; }

    /// <summary>Index into the generic adjustor thunk table.</summary>
    public int AdjustorThunkTableIndex { get; init; }
}

/// <summary>
/// Il2CppMethodSpec — identifies a specific generic method instantiation.
///
/// Source Path: WriteIl2CppGenericMethodDefinitions.cs lines 47-53:
///   "{ " + GetMethodIndex(method.Resolve()) + ", "
///        + classInstIndex_or_minus1 + ", "
///        + methodInstIndex_or_minus1 + " }"
///
/// Binary layout: 3 × int32 = 12 bytes
/// </summary>
public readonly struct MethodSpecDef
{
    /// <summary>Index into the MethodDefinitions array.</summary>
    public int MethodDefinitionIndex { get; init; }

    /// <summary>
    /// Index into the GenericInst table for the declaring type's generic args.
    /// -1 if the declaring type is not generic.
    /// </summary>
    public int ClassInstIndex { get; init; }

    /// <summary>
    /// Index into the GenericInst table for the method's own generic args.
    /// -1 if the method is not generic.
    /// </summary>
    public int MethodInstIndex { get; init; }
}
