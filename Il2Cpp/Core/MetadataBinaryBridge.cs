using Rosetta.Binary;
using Rosetta.Metadata;

namespace Rosetta.Core;

/// <summary>
/// The Bridge: Maps methods from global-metadata.dat to physical
/// method pointer addresses in libil2cpp.so.
///
/// Source of Truth:
///   - PerAssemblyCodeMetadataWriter.cs line 19-21:
///       Methods within each assembly are sorted by MetadataToken.RID.
///       This means s_methodPointers[0..N] corresponds to methods
///       with increasing RID within that assembly.
///
///   - Il2CppImageDefinition.cs:
///       Each Image has TypeStart/TypeCount telling us which
///       TypeDefinitions (and thus which Methods) belong to it.
///
///   - The Image.Name (from MetadataStrings) matches the
///     CodeGenModule.moduleName string — this is the join key.
///
/// Algorithm:
///   1. For each Image, collect all global method indices belonging to it
///      by iterating its TypeDefs and their MethodStart/MethodCount.
///   2. Sort those indices by method metadata token RID (= position within
///      the Methods section for this assembly, which == RID order).
///   3. Match the Image.Name with a CodeGenModule.moduleName.
///   4. Map: sortedMethods[j] → CodeGenModule.methodPointers[j]
/// </summary>
public sealed class MetadataBinaryBridge
{
    private readonly MetadataParser _metadata;
    private readonly IBinaryParser _elf;
    private readonly RegistrationResolver _registration;
    private readonly bool _isArm32;

    /// <summary>
    /// Resolved mappings: method global index → virtual address.
    /// </summary>
    public Dictionary<int, ulong> MethodAddressMap { get; } = new();

    /// <summary>
    /// Set of method VAs whose raw pointers had bit 0 = 1 (Thumb mode).
    /// Methods NOT in this set on an ARM32 binary are in ARM mode.
    /// </summary>
    public HashSet<ulong> ThumbMethodAddresses { get; } = new();

    /// <summary>
    /// Module name → method pointers from RegistrationResolver.
    /// </summary>
    public IReadOnlyDictionary<string, ulong[]> ModuleMethodPointers => _registration.ModuleMethodPointers;

    /// <summary>Expose RegistrationResolver for lifter access to types and ELF data.</summary>
    public RegistrationResolver Registration => _registration;

    public MetadataBinaryBridge(MetadataParser metadata, IBinaryParser elf, RegistrationResolver registration)
    {
        _metadata = metadata;
        _elf = elf;
        _registration = registration;
        _isArm32 = elf.Is32Bit;
    }

    /// <summary>
    /// Build the method → address map using per-image mapping.
    ///
    /// Source: PerAssemblyCodeMetadataWriter.cs lines 19-21:
    ///   MethodDefinition[] array = (from m in assembly.AllMethods()
    ///       orderby m.MetadataToken.RID
    ///       select m).ToArray();
    ///
    /// The method pointers array for each module is indexed in RID order.
    /// The global MethodDefinitions are stored in RID order per assembly
    /// within the Methods section of global-metadata.dat.
    ///
    /// CRITICAL: The token RID for method 0x06 table = 1-based position
    /// within the assembly. MethodDefinitions within an image are already
    /// in RID order (since the metadata writer serializes them in TypeDef
    /// order, and TypeDefs are in metadata token order).
    /// </summary>
    public void BuildMap()
    {
        // Build a lookup: imageName → method pointers from binary
        var binaryModules = _registration.ModuleMethodPointers;

        if (binaryModules.Count == 0)
        {
            // Fallback: no registration resolved, nothing to map
            return;
        }

        // For each Image in the metadata
        for (int imgIdx = 0; imgIdx < _metadata.ImageDefinitions.Length; imgIdx++)
        {
            var image = _metadata.ImageDefinitions[imgIdx];
            string? imageName = image.Name;
            if (string.IsNullOrEmpty(imageName)) continue;

            // Find matching CodeGenModule by name
            if (!binaryModules.TryGetValue(imageName, out ulong[]? modulePointers))
                continue;

            // Collect all global method indices for this image
            // Source: Image.TypeStart..TypeStart+TypeCount → each TypeDef.MethodStart..MethodCount
            var methodGlobalIndices = new List<int>();

            int typeEnd = image.TypeStart + (int)image.TypeCount;
            for (int typeIdx = image.TypeStart; typeIdx < typeEnd && typeIdx < _metadata.TypeDefinitions.Length; typeIdx++)
            {
                if (typeIdx < 0) continue;
                var td = _metadata.TypeDefinitions[typeIdx];
                for (int m = 0; m < td.MethodCount; m++)
                {
                    int globalMethodIdx = td.MethodStart + m;
                    if (globalMethodIdx >= 0 && globalMethodIdx < _metadata.MethodDefinitions.Length)
                    {
                        methodGlobalIndices.Add(globalMethodIdx);
                    }
                }
            }

            // Source: PerAssemblyCodeMetadataWriter.cs line 19-21:
            //   MethodDefinition[] array = (from m in assembly.AllMethods()
            //       orderby m.MetadataToken.RID select m).ToArray();
            //
            //   RID = (Token & 0x00FFFFFF).
            //
            // CRITICAL: The method pointer array is sorted by MetadataToken.RID across the
            // entire assembly. TypeDef iteration doesn't guarantee RID order for nested types.
            // We MUST sort by RID to match the pointer array ordering.

            // Sort all methods by RID
            methodGlobalIndices.Sort((a, b) =>
            {
                uint ridA = _metadata.MethodDefinitions[a].Token & 0x00FFFFFF;
                uint ridB = _metadata.MethodDefinitions[b].Token & 0x00FFFFFF;
                return ridA.CompareTo(ridB);
            });

            // Diagnostic: log any count mismatch
            int validPointers = 0;
            foreach (var p in modulePointers) if (p != 0) validPointers++;
            
            int diff = methodGlobalIndices.Count - modulePointers.Length;
            if (Rosetta.Pipeline.ConsoleReporter.IsTracing)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"    [{imgIdx:D2}] {imageName,-48} methods={methodGlobalIndices.Count} pointers={modulePointers.Length} valid={validPointers} delta={diff:+#;-#;0}");
                Console.ResetColor();
            }

            // Map each method — methodGlobalIndices[j] maps to modulePointers[j]
            // ARM32 note: method pointers have bit 0 = 1 (Thumb indicator).
            // BL target addresses don't include this bit, so strip it for consistency.
            int limit = Math.Min(methodGlobalIndices.Count, modulePointers.Length);
            for (int j = 0; j < limit; j++)
            {
                ulong ptr = modulePointers[j];
                if (ptr != 0)
                {
                    if (_isArm32)
                    {
                        if ((ptr & 1UL) != 0)
                            ThumbMethodAddresses.Add(ptr & ~1UL);
                        ptr &= ~1UL; // Strip Thumb bit
                    }
                    MethodAddressMap[methodGlobalIndices[j]] = ptr;
                }
            }
        }

        // Phase 2: Resolve generic method instantiations
        BuildGenericMethodMap();
    }

    // ====================================================================
    // Generic Method Resolution
    //
    // Source chain:
    //   GenericMethodTable[i].GenericMethodIndex → MethodSpecs[idx].MethodDefinitionIndex
    //   GenericMethodTable[i].PointerTableIndex  → GenericMethodPointers[ptrIdx] → VA
    //
    // Source: WriteIl2CppGenericMethodTable.cs line 20:
    //   { TableIndex, PointerTableIndex, InvokerIndex, AdjustorThunkTableIndex }
    //
    // Source: WriteIl2CppGenericMethodDefinitions.cs lines 47-53:
    //   { methodDefinitionIndex, classInstIndex, methodInstIndex }
    //
    // Mapping Logic:
    //   For each GenericMethodFuncDef, look up the MethodSpec to get the
    //   MethodDefinition index, then look up the pointer to get the VA.
    //   If the method definition doesn't already have an address (from
    //   per-module pointers), assign this one. If it already has one,
    //   this is just another instantiation sharing the same definition.
    // ====================================================================
    private void BuildGenericMethodMap()
    {
        var gmTable = _registration.GenericMethodTable;
        var specs = _registration.MethodSpecs;
        var pointers = _registration.GenericMethodPointers;

        if (gmTable.Length == 0 || specs.Length == 0 || pointers.Length == 0)
            return;

        int resolved = 0;
        for (int i = 0; i < gmTable.Length; i++)
        {
            var entry = gmTable[i];

            // Validate MethodSpec index
            if (entry.GenericMethodIndex < 0 || entry.GenericMethodIndex >= specs.Length)
                continue;

            var spec = specs[entry.GenericMethodIndex];

            // Validate MethodDefinition index
            if (spec.MethodDefinitionIndex < 0 || spec.MethodDefinitionIndex >= _metadata.MethodDefinitions.Length)
                continue;

            // Validate pointer table index
            if (entry.PointerTableIndex < 0 || entry.PointerTableIndex >= pointers.Length)
                continue;

            ulong addr = pointers[entry.PointerTableIndex];
            if (addr == 0)
                continue;

            if (_isArm32)
            {
                if ((addr & 1UL) != 0)
                    ThumbMethodAddresses.Add(addr & ~1UL);
                addr &= ~1UL; // Strip Thumb bit
            }

            // Only assign if this MethodDefinition doesn't already have an address
            if (!MethodAddressMap.ContainsKey(spec.MethodDefinitionIndex))
            {
                MethodAddressMap[spec.MethodDefinitionIndex] = addr;
                resolved++;
            }
        }
    }

    /// <summary>
    /// Look up the virtual address for a given method definition.
    /// </summary>
    public ulong? GetMethodAddress(MethodDefinition method)
    {
        return MethodAddressMap.TryGetValue(method.GlobalIndex, out var addr) ? addr : null;
    }

    /// <summary>
    /// Get the declaring type name for a method.
    /// </summary>
    public string GetDeclaringTypeName(MethodDefinition method)
    {
        int typeIdx = method.DeclaringTypeIndex;
        if (typeIdx >= 0 && typeIdx < _metadata.TypeDefinitions.Length)
            return _metadata.TypeDefinitions[typeIdx].FullName;
        return "<unknown_type>";
    }

    /// <summary>
    /// Generate a fully qualified method signature string (untyped).
    /// </summary>
    public string GetFullMethodSignature(MethodDefinition method)
    {
        string typeName = GetDeclaringTypeName(method);
        string methodName = method.Name ?? $"Method_{method.GlobalIndex}";
        return $"{typeName}::{methodName}";
    }

    /// <summary>
    /// Generate a fully typed C# method signature:
    ///   returnType DeclaringType::MethodName(paramType0 p0, paramType1 p1, ...)
    ///
    /// Source: MethodDefinition.ReturnTypeIndex indexes into g_Il2CppTypeTable.
    /// Source: MethodDefinition.ParameterStart/ParameterCount point to
    ///         ParameterDefinition entries, each with their own TypeIndex.
    /// </summary>
    public string GetTypedMethodSignature(MethodDefinition method, TypeResolver typeResolver)
    {
        string typeName = GetDeclaringTypeName(method);
        string methodName = method.Name ?? $"Method_{method.GlobalIndex}";

        // Return type
        string retType = method.ReturnTypeIndex >= 0
            ? typeResolver.ResolveTypeName(method.ReturnTypeIndex)
            : "void";

        // Parameters
        var paramParts = new List<string>();
        for (int p = 0; p < method.ParameterCount; p++)
        {
            int paramIdx = method.ParameterStart + p;
            if (paramIdx >= 0 && paramIdx < _metadata.ParameterDefinitions.Length)
            {
                var pd = _metadata.ParameterDefinitions[paramIdx];
                string paramType = typeResolver.ResolveTypeName(pd.TypeIndex);
                string paramName = pd.Name ?? $"p{p}";
                paramParts.Add($"{paramType} {paramName}");
            }
        }

        string paramStr = string.Join(", ", paramParts);
        return $"{retType} {typeName}::{methodName}({paramStr})";
    }
}
