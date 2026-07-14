using Rosetta.Binary;
using Rosetta.Common;
using Rosetta.Metadata;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.Resolve;

/// <summary>Data for a resolved generic method instantiation.</summary>
public readonly struct GenericMethodInfo
{
    public string FullName { get; init; }
    public int ParameterCount { get; init; }
    public int FpArgCount { get; init; }
    public int FpParamCount { get; init; }
    public bool HasDoubleArgs { get; init; }
    public bool IsStatic { get; init; }
    public bool IsVoid { get; init; }
    public int MethodIndex { get; init; }
    public int ReturnHfaSize { get; init; }
}

/// <summary>
/// Resolves generic method instantiation addresses to their MethodDefinition names.
///
/// Source Evidence Chain:
///   1. WriteIl2CppGenericMethodTable.cs line 20:
///      GenericMethodFunctionsDefinitions[i] = { TableIndex, PointerTableIndex, InvokerIndex, AdjustorThunkTableIndex }
///   
///   2. WriteIl2CppGenericMethodDefinitions.cs lines 47-53:
///      MethodSpec[TableIndex] = { MethodDefinitionIndex, ClassInstIndex, MethodInstIndex }
///   
///   3. CodeRegistrationWriter.cs line 108-109:
///      g_Il2CppGenericMethodPointers[PointerTableIndex] → native function VA
///
/// The Problem:
///   MetadataBinaryBridge.BuildGenericMethodMap() only stores ONE address per MethodDefinition.
///   But generic sharing can produce MULTIPLE unique native addresses for the same MethodDefinition
///   (e.g., List<int>.Add and List<string>.Add may share the same definition but have different VAs).
///   These "lost" addresses appear as bare BL calls with no annotation.
///
/// The Solution:
///   Build a complete VA → method name map from the GenericMethodPointers table,
///   preserving ALL unique VAs. This is used by CallResolver as a secondary lookup.
/// </summary>
public sealed class GenericInstanceResolver
{
    /// <summary>VA → resolved generic method info.</summary>
    private readonly Dictionary<ulong, GenericMethodInfo> _addressToInfo = new();

    /// <summary>Number of successfully resolved generic instantiation addresses.</summary>
    public int ResolvedCount => _addressToInfo.Count;

    /// <summary>
    /// Build the complete generic method VA → name map.
    /// </summary>
    public void Build(
        MetadataParser metadata,
        RegistrationResolver registration,
        TypeResolver? typeResolver = null,
        bool isArm32 = false)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"GenericInstanceResolver.Build()");
        var gmTable = registration.GenericMethodTable;
        var specs = registration.MethodSpecs;
        var pointers = registration.GenericMethodPointers;

        if (gmTable.Length == 0 || specs.Length == 0 || pointers.Length == 0)
            return;

        for (int i = 0; i < gmTable.Length; i++)
        {
            var entry = gmTable[i];

            // Validate MethodSpec index
            if (entry.GenericMethodIndex < 0 || entry.GenericMethodIndex >= specs.Length)
                continue;

            var spec = specs[entry.GenericMethodIndex];

            // Validate MethodDefinition index
            if (spec.MethodDefinitionIndex < 0 || spec.MethodDefinitionIndex >= metadata.MethodDefinitions.Length)
                continue;

            // Validate pointer table index
            if (entry.PointerTableIndex < 0 || entry.PointerTableIndex >= pointers.Length)
                continue;

            ulong addr = pointers[entry.PointerTableIndex];
            if (addr == 0)
                continue;
            if (isArm32) addr &= ~1UL; // Strip Thumb bit

            // Don't overwrite if already registered (first entry wins for same VA)
            if (_addressToInfo.ContainsKey(addr))
                continue;

            var md = metadata.MethodDefinitions[spec.MethodDefinitionIndex];
            string methodName = md.Name ?? $"Method_{spec.MethodDefinitionIndex}";

            // Build qualified name with declaring type
            string? typeName = null;
            if (md.DeclaringTypeIndex >= 0 && md.DeclaringTypeIndex < metadata.TypeDefinitions.Length)
            {
                typeName = metadata.TypeDefinitions[md.DeclaringTypeIndex].FullName;
            }

            // Resolve method-level generic args (e.g., GetComponent<AudioSource>)
            if (typeResolver != null && spec.MethodInstIndex >= 0)
            {
                string? methodArgs = typeResolver.ResolveGenericInstArgs(spec.MethodInstIndex);
                if (methodArgs != null)
                    methodName += methodArgs;
            }

            // Resolve class-level generic args (e.g., List<int>::Add)
            if (typeResolver != null && spec.ClassInstIndex >= 0 && typeName != null)
            {
                string? classArgs = typeResolver.ResolveGenericInstArgs(spec.ClassInstIndex);
                if (classArgs != null)
                {
                    int backtick = typeName.IndexOf('`');
                    if (backtick >= 0)
                        typeName = typeName[..backtick];
                    typeName += classArgs;
                }
            }

            Il2CppType[]? classInstTypes = spec.ClassInstIndex >= 0 && typeResolver != null ? typeResolver.GetGenericInstTypes(spec.ClassInstIndex) : null;
            Il2CppType[]? methodInstTypes = spec.MethodInstIndex >= 0 && typeResolver != null ? typeResolver.GetGenericInstTypes(spec.MethodInstIndex) : null;

            int fpCount = 0;
            int fpParamCount = 0;
            bool hasDouble = false;

            if (typeResolver != null)
            {
                for (int p = 0; p < md.ParameterCount; p++)
                {
                    int paramIdx = md.ParameterStart + p;
                    if (paramIdx < 0 || paramIdx >= metadata.ParameterDefinitions.Length)
                        continue;
                    var pd = metadata.ParameterDefinitions[paramIdx];
                    var il2cppType = typeResolver.GetTypeByIndex(pd.TypeIndex);
                    if (il2cppType == null) continue;

                    var resolvedType = typeResolver.ResolveGenericType(il2cppType.Value, classInstTypes, methodInstTypes);

                    if (resolvedType.ByRef) continue;

                    if (resolvedType.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_R4)
                    {
                        fpCount++;
                        fpParamCount++;
                    }
                    else if (resolvedType.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_R8)
                    {
                        fpCount++;
                        fpParamCount++;
                        hasDouble = true;
                    }
                    else if (resolvedType.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
                    {
                        int hfaRegs = typeResolver.GetHfaSize(resolvedType);
                        if (hfaRegs > 0)
                        {
                            fpCount += hfaRegs;
                            fpParamCount++;
                        }
                    }
                }
            }

            bool isVoid = false;
            int returnHfaSize = 0;
            if (typeResolver != null && md.ReturnTypeIndex >= 0)
            {
                var retType = typeResolver.GetTypeByIndex(md.ReturnTypeIndex);
                if (retType != null)
                {
                    var resolvedRet = typeResolver.ResolveGenericType(retType.Value, classInstTypes, methodInstTypes);
                    isVoid = resolvedRet.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_VOID;
                    if (!isVoid)
                    {
                        returnHfaSize = typeResolver.GetHfaSize(resolvedRet);
                    }
                }
            }

            string fullName = typeName != null ? $"{typeName}::{methodName}" : methodName;

            _addressToInfo[addr] = new GenericMethodInfo
            {
                FullName = fullName,
                ParameterCount = md.ParameterCount,
                FpArgCount = fpCount,
                FpParamCount = fpParamCount,
                HasDoubleArgs = hasDouble,
                IsStatic = (md.Flags & 0x0010) != 0,
                IsVoid = isVoid,
                MethodIndex = spec.MethodDefinitionIndex,
                ReturnHfaSize = returnHfaSize
            };
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  GenericInstanceResolver: {_addressToInfo.Count} instances resolved");
    }

    /// <summary>
    /// Try to resolve a virtual address to a generic method instantiation.
    /// </summary>
    public GenericMethodInfo? TryResolve(ulong address)
    {
        return _addressToInfo.TryGetValue(address, out var info) ? info : null;
    }
}

