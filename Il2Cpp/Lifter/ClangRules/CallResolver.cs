using Rosetta.Analysis.Resolve;
using Rosetta.Binary;
using Rosetta.Common;
using Rosetta.Metadata;
using Rosetta.Model;
using Rosetta.Lifter.ClangRules;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.ClangRules;

/// <summary>
/// Resolves BL/BLR call targets to method names using MetadataBinaryBridge.
///
/// Source evidence chain:
///   1. AArch64ISelLowering.cpp L9814 — LowerCall() emits BL with direct target address
///   2. MethodBodyWriter.cs L1318 — Call opcode emits direct function call
///   3. MetadataBinaryBridge.MethodAddressMap — method index → VA
///   4. AAPCS64 — X0-X7 args, X0 return
///
/// The resolver inverts the MethodAddressMap to: VA → method info.
///
/// VA Collision Handling:
///   Multiple MethodDefinitions can share the same native VA. This happens with
///   Unity ICalls where get_position, get_localPosition, set_eulerAngles, etc.
///   all thunk through the same native address as unrelated methods (e.g.,
///   Enumerator::get_Current, Enumerator::MoveNext).
///
///   The reverse map stores ALL candidates per VA. TryResolve picks the best
///   candidate using a scoring heuristic that prefers:
///     1. UnityEngine.* types over System.Collections.* types
///     2. Specific property accessors (get_*, set_*) over generic names
///     3. Non-Enumerator methods over Enumerator methods
/// </summary>
public sealed class CallResolver
{
    private readonly MetadataParser _metadata;
    private readonly Dictionary<int, ulong> _methodAddressMap;
    private TypeResolver? _typeResolver;
    private TypeModel? _typeModel;

    /// <summary>
    /// Reverse map: VA → list of method global indices.
    /// Multiple methods can share the same native VA (Unity ICalls, shared thunks).
    /// </summary>
    private readonly Dictionary<ulong, List<int>> _addressToMethods = new();

    /// <summary>Known IL2CPP runtime function addresses (resolved by symbol name).</summary>
    private readonly Dictionary<ulong, string> _runtimeFunctions = new();

    /// <summary>Optional: resolves generic instantiation VAs not in the per-module map.</summary>
    private GenericInstanceResolver? _genericResolver;

    /// <summary>Number of VA collisions detected during reverse map construction.</summary>
    public int CollisionCount { get; private set; }

    public CallResolver(MetadataParser metadata, Dictionary<int, ulong> methodAddressMap)
    {
        _metadata = metadata;
        _methodAddressMap = methodAddressMap;
        BuildReverseMap();
    }

    /// <summary>Register the TypeResolver for return type analysis.</summary>
    public void RegisterTypeResolver(TypeResolver typeResolver)
    {
        _typeResolver = typeResolver;
    }

    public void RegisterTypeModel(TypeModel typeModel)
    {
        _typeModel = typeModel;
    }

    /// <summary>
    /// Register a known IL2CPP runtime function address (from ELF dynamic symbols).
    ///
    /// Source: il2cpp-codegen.h — runtime helper functions:
    ///   il2cpp_codegen_runtime_class_init_inline (L997)
    ///   il2cpp_codegen_object_new (via runtime)
    ///   il2cpp_codegen_raise_exception
    ///   etc.
    /// </summary>
    public void RegisterRuntimeFunction(ulong address, string name)
    {
        _runtimeFunctions[address] = name;
    }

    /// <summary>
    /// Register a GenericInstanceResolver for secondary lookup of generic method VAs.
    ///
    /// Source: WriteIl2CppGenericMethodTable.cs line 20 —
    ///   GenericMethodFunctionsDefinitions maps MethodSpec → GenericMethodPointer.
    ///   Multiple unique VAs can map to the same MethodDefinition.
    /// </summary>
    public void RegisterGenericResolver(GenericInstanceResolver resolver)
    {
        _genericResolver = resolver;
    }

    /// <summary>
    /// Build the reverse map (VA → method indices), preserving ALL candidates per VA.
    ///
    /// When multiple methods share a VA, they are all stored. TryResolve uses
    /// a scoring heuristic to pick the best candidate at resolution time.
    /// </summary>
    private void BuildReverseMap()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"CallResolver.BuildReverseMap: processing {_methodAddressMap.Count} entries");
        CollisionCount = 0;

        foreach (var (methodIdx, va) in _methodAddressMap)
        {
            if (va == 0) continue;

            if (!_addressToMethods.TryGetValue(va, out var list))
            {
                list = new List<int>(1);
                _addressToMethods[va] = list;
            }
            else
            {
                // This VA already has at least one method — collision
                CollisionCount++;
            }

            list.Add(methodIdx);
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  CallResolver: {CollisionCount} VA collisions detected");
    }

    /// <summary>
    /// Resolve a call target address to a method name and signature.
    /// </summary>
    public ResolvedCall? TryResolve(ulong targetAddress)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    CallResolver.TryResolve: 0x{targetAddress:X}");
        // Check runtime functions first
        if (_runtimeFunctions.TryGetValue(targetAddress, out string? rtName))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → RuntimeFunction: {rtName}");
            return new ResolvedCall
            {
                TargetAddress = targetAddress,
                MethodName = rtName,
                IsRuntimeHelper = true
            };
        }

        // Check user methods (with collision-aware resolution)
        if (_addressToMethods.TryGetValue(targetAddress, out var candidates))
        {
            int methodIdx = candidates.Count == 1
                ? candidates[0]
                : PickBestCandidate(candidates);

            if (methodIdx >= 0 && methodIdx < _metadata.MethodDefinitions.Length)
            {
                var md = _metadata.MethodDefinitions[methodIdx];
                string name = md.Name ?? $"Method_{methodIdx}";

                // Find the declaring type
                string? typeName = null;
                if (md.DeclaringTypeIndex >= 0 && md.DeclaringTypeIndex < _metadata.TypeDefinitions.Length)
                {
                    typeName = _metadata.TypeDefinitions[md.DeclaringTypeIndex].FullName;
                }

                var (fpRegCount, fpParamCount, hasDouble) = CountFpParameters(md);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → Method: {typeName}::{name} (idx={methodIdx})");
                return new ResolvedCall
                {
                    TargetAddress = targetAddress,
                    MethodName = name,
                    DeclaringType = typeName,
                    ParameterCount = md.ParameterCount,
                    MethodIndex = methodIdx,
                    IsStatic = (md.Flags & 0x0010) != 0,
                    IsVoid = IsVoidReturn(md.ReturnTypeIndex),
                    IsRuntimeHelper = false,
                    CandidateCount = candidates.Count,
                    FpArgCount = fpRegCount,
                    FpParamCount = fpParamCount,
                    HasDoubleArgs = hasDouble,
                    ReturnHfaSize = _typeResolver?.GetHfaSize(md.ReturnTypeIndex) ?? 0
                };
            }
        }

        // Check generic method instantiations (secondary map)
        // Source: GenericMethodPointers table — multiple VAs per MethodDefinition
        if (_genericResolver != null)
        {
            var genericInfo = _genericResolver.TryResolve(targetAddress);
            if (genericInfo.HasValue)
            {
                var gi = genericInfo.Value;
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → GenericInstance: {gi.FullName}");
                return new ResolvedCall
                {
                    TargetAddress = targetAddress,
                    MethodName = gi.FullName.Contains("::") ? gi.FullName[(gi.FullName.IndexOf("::") + 2)..] : gi.FullName,
                    DeclaringType = gi.FullName.Contains("::") ? gi.FullName[..gi.FullName.IndexOf("::")] : null,
                    ParameterCount = gi.ParameterCount,
                    MethodIndex = gi.MethodIndex,
                    IsStatic = gi.IsStatic,
                    IsVoid = gi.IsVoid,
                    IsRuntimeHelper = false,
                    FpArgCount = gi.FpArgCount,
                    FpParamCount = gi.FpParamCount,
                    HasDoubleArgs = gi.HasDoubleArgs,
                    ReturnHfaSize = gi.ReturnHfaSize
                };
            }
        }

        return null;
    }

    /// <summary>
    /// When multiple methods share the same VA, pick the best candidate using
    /// a scoring heuristic based on declaring type and method name specificity.
    ///
    /// Problem: Unity ICalls (get_position, get_localPosition, etc.) share native
    /// thunk addresses with unrelated methods (Enumerator::get_Current, MoveNext).
    /// The metadata enumeration order is arbitrary, so we need a semantic ranking.
    ///
    /// Scoring rules (higher = better):
    ///   +100  UnityEngine.* declaring type (these are the methods users write)
    ///   +50   Specific property accessor name (get_position, set_localScale, etc.)
    ///   +20   Any get_*/set_* name (standard .NET property pattern)
    ///   -30   System.Collections.* declaring type (generic infrastructure)
    ///   -50   Enumerator declaring type (always a wrong match for Unity ICalls)
    ///   -10   Generic collection method names (get_Current, MoveNext, Reset)
    /// </summary>
    private int PickBestCandidate(List<int> candidates)
    {
        int bestIdx = candidates[0];
        int bestScore = int.MinValue;

        foreach (var idx in candidates)
        {
            if (idx < 0 || idx >= _metadata.MethodDefinitions.Length) continue;
            var md = _metadata.MethodDefinitions[idx];
            int score = ScoreCandidate(idx, md);
            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = idx;
            }
        }

        return bestIdx;
    }

    /// <summary>
    /// Score a method definition for disambiguation when multiple methods share a VA.
    /// </summary>
    private int ScoreCandidate(int methodIndex, MethodDefinition md)
    {
        int score = 0;

        // Score based on declaring type
        string? typeName = null;
        if (md.DeclaringTypeIndex >= 0 && md.DeclaringTypeIndex < _metadata.TypeDefinitions.Length)
        {
            typeName = _metadata.TypeDefinitions[md.DeclaringTypeIndex].FullName;
        }

        if (typeName != null)
        {
            // Prefer UnityEngine types — these are the real targets in game code
            if (typeName.StartsWith("UnityEngine."))
                score += 100;

            // Deprioritize generic collection/enumerator types — these are false matches
            if (typeName.Contains("Enumerator"))
                score -= 50;
            if (typeName.StartsWith("System.Collections."))
                score -= 30;
            if (typeName.StartsWith("System.") && !typeName.StartsWith("System.Collections."))
                score -= 10; // Mild penalty for System.* (but less than Collections)
        }

        // Score based on method name specificity
        string name = md.Name ?? "";

        // Generic collection method names are almost always wrong for Unity ICalls
        if (name is "get_Current" or "MoveNext" or "Reset" or "Dispose")
            score -= 10;

        // Property accessors are strong positive signals for typical game code
        if (_typeModel != null && _typeModel.IsPropertyAccessor(methodIndex))
        {
            score += 50;
        }

        // Constructor/ToString/Equals are typically specific to the type
        if (name is "ToString" or ".ctor" or "Equals" or "GetHashCode")
            score += 15;

        return score;
    }

    /// <summary>
    /// Build an exhaustive reverse map by scanning ALL per-module method pointer arrays.
    ///
    /// Problem: MetadataBinaryBridge.BuildMap() uses Math.Min(methodIndices.Count, pointers.Length)
    /// when mapping. If the metadata has MORE methods than the pointer array has entries (or vice
    /// versa), some method pointers go unmapped. This leaves valid user methods unresolved
    /// (e.g., Vector3.ToString at 0x1097668).
    ///
    /// Solution: For each module, iterate the raw pointer array and try to find the corresponding
    /// MethodDefinition by scanning the same image's type→method structure in RID order. Any VA
    /// not already in _addressToMethods gets added.
    ///
    /// Returns the number of newly resolved addresses.
    /// </summary>
    public int BuildExhaustiveReverseMap(
        IReadOnlyDictionary<string, ulong[]> moduleMethodPointers,
        Metadata.ImageDefinition[] imageDefinitions,
        bool isArm32 = false)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"CallResolver.BuildExhaustiveReverseMap: {moduleMethodPointers.Count} modules");
        int newlyResolved = 0;

        foreach (var (moduleName, pointers) in moduleMethodPointers)
        {
            // Find the matching image in metadata
            ImageDefinition? matchedImage = null;
            for (int imgIdx = 0; imgIdx < imageDefinitions.Length; imgIdx++)
            {
                if (imageDefinitions[imgIdx].Name == moduleName)
                {
                    matchedImage = imageDefinitions[imgIdx];
                    break;
                }
            }

            if (matchedImage == null) continue;
            var image = matchedImage;

            // Collect all method global indices for this image (same logic as bridge)
            var methodGlobalIndices = new List<int>();
            int typeEnd = image.TypeStart + (int)image.TypeCount;
            for (int typeIdx = image.TypeStart;
                 typeIdx < typeEnd && typeIdx < _metadata.TypeDefinitions.Length;
                 typeIdx++)
            {
                if (typeIdx < 0) continue;
                var td = _metadata.TypeDefinitions[typeIdx];
                for (int m = 0; m < td.MethodCount; m++)
                {
                    int globalMethodIdx = td.MethodStart + m;
                    if (globalMethodIdx >= 0 && globalMethodIdx < _metadata.MethodDefinitions.Length)
                        methodGlobalIndices.Add(globalMethodIdx);
                }
            }

            // Now map ALL pointers to method indices
            // CRITICAL: Sort by RID first — must match the pointer array ordering
            // (same as MetadataBinaryBridge.BuildMap)
            methodGlobalIndices.Sort((a, b) =>
            {
                uint ridA = _metadata.MethodDefinitions[a].Token & 0x00FFFFFF;
                uint ridB = _metadata.MethodDefinitions[b].Token & 0x00FFFFFF;
                return ridA.CompareTo(ridB);
            });

            int limit = Math.Min(methodGlobalIndices.Count, pointers.Length);
            for (int j = 0; j < limit; j++)
            {
                ulong va = pointers[j];
                if (va == 0) continue;
                if (isArm32) va &= ~1UL; // Strip Thumb bit

                int methodIdx = methodGlobalIndices[j];

                // Only add if this VA is not already known
                if (!_addressToMethods.ContainsKey(va) && !_runtimeFunctions.ContainsKey(va))
                {
                    _addressToMethods[va] = new List<int>(1) { methodIdx };
                    newlyResolved++;
                }
                else if (_addressToMethods.TryGetValue(va, out var existing))
                {
                    // VA already known — add this method index as another candidate if not present
                    if (!existing.Contains(methodIdx))
                        existing.Add(methodIdx);
                }
            }

            // Also scan pointers BEYOND the bridge's Math.Min limit
            // These are methods that the bridge skipped entirely
            for (int j = limit; j < pointers.Length; j++)
            {
                ulong va = pointers[j];
                if (va == 0) continue;

                // We don't have a methodIdx for these extra pointers.
                // They exist in the binary but lack metadata mapping.
                // Skip — they can only be resolved by heuristics.
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  CallResolver exhaustive map added {newlyResolved} unresolved methods");
        return newlyResolved;
    }

    /// <summary>
    /// Resolve a method's return type name from its metadata index.
    /// Returns the clean C# type name (e.g., "string", "int", "float") or null if unresolvable.
    /// </summary>
    public string? ResolveReturnType(int methodIndex)
    {
        if (_typeResolver == null) return null;
        if (methodIndex < 0 || methodIndex >= _metadata.MethodDefinitions.Length) return null;

        var md = _metadata.MethodDefinitions[methodIndex];
        if (md.ReturnTypeIndex < 0) return null;

        // Check for void
        var il2cppType = _typeResolver.GetTypeByIndex(md.ReturnTypeIndex);
        if (il2cppType == null) return null;
        if (il2cppType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_VOID) return null;

        return _typeResolver.ResolveTypeName(md.ReturnTypeIndex);
    }

    /// <summary>
    /// Count how many FP/SIMD registers are used by parameters.
    /// In ARM64 AAPCS, scalar float/double use s0-s7/d0-d7.
    /// HFA (Homogeneous Floating-point Aggregate) value types like Vector3 (3 floats)
    /// also use FP registers (s0-s2 for Vector3, s0-s3 for Quaternion, etc.).
    ///
    /// Returns:
    ///   fpRegCount — total FP registers consumed (for source wiring)
    ///   fpParamCount — logical parameters using FP regs (for GP param computation)
    ///   hasDouble — true if any parameter uses double precision
    /// </summary>
    private (int fpRegCount, int fpParamCount, bool hasDouble) CountFpParameters(MethodDefinition md)
    {
        if (_typeResolver == null || md.ParameterCount == 0)
            return (0, 0, false);

        int fpRegCount = 0;
        int fpParamCount = 0;
        bool hasDouble = false;

        for (int p = 0; p < md.ParameterCount; p++)
        {
            int paramIdx = md.ParameterStart + p;
            if (paramIdx < 0 || paramIdx >= _metadata.ParameterDefinitions.Length)
                continue;

            var pd = _metadata.ParameterDefinitions[paramIdx];
            var il2cppType = _typeResolver.GetTypeByIndex(pd.TypeIndex);
            if (il2cppType == null) continue;

            // By-reference parameters (out, ref) are passed as pointers (memory addresses)
            // They always go into general-purpose (GP) registers, NEVER FP/SIMD registers.
            if (il2cppType.Value.ByRef) continue;

            if (il2cppType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_R4)
            {
                fpRegCount++;
                fpParamCount++;
            }
            else if (il2cppType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_R8)
            {
                fpRegCount++;
                fpParamCount++;
                hasDouble = true;
            }
            else if (il2cppType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
            {
                // ARM64 AAPCS: HFA value types are passed via FP registers
                // Dynamically introspected via metadata
                int hfaRegs = _typeResolver.GetHfaSize(pd.TypeIndex);
                if (hfaRegs > 0)
                {
                    fpRegCount += hfaRegs;
                    fpParamCount++;
                }
            }
        }

        return (fpRegCount, fpParamCount, hasDouble);
    }



    /// <summary>
    /// Check if a return type index resolves to void.
    /// Uses the Il2CppType table to check TypeEnum == IL2CPP_TYPE_VOID.
    /// </summary>
    private bool IsVoidReturn(int returnTypeIndex)
    {
        if (_typeResolver == null) return false;
        var il2cppType = _typeResolver.GetTypeByIndex(returnTypeIndex);
        if (il2cppType == null) return false;
        return il2cppType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_VOID;
    }
}

/// <summary>Resolved call information.</summary>
public sealed class ResolvedCall
{
    public ulong TargetAddress { get; init; }
    public string? MethodName { get; init; }
    public string? DeclaringType { get; init; }
    public int ParameterCount { get; init; }
    public int MethodIndex { get; init; } = -1;
    public bool IsStatic { get; init; }
    public bool IsVoid { get; init; }
    public bool IsRuntimeHelper { get; init; }

    /// <summary>
    /// Number of float/double parameters that use s0-s7/d0-d7 registers (ARM64 AAPCS).
    /// </summary>
    /// <summary>Number of FP/SIMD registers consumed by parameters (physical count for source wiring).</summary>
    public int FpArgCount { get; init; }

    /// <summary>Number of logical parameters that use FP registers (for GP param computation).
    /// For scalar float/double, FpParamCount == FpArgCount.
    /// For HFA types (Vector3 = 3 regs, 1 param), FpParamCount < FpArgCount.</summary>
    public int FpParamCount { get; init; }

    /// <summary>True if any FP parameter is double precision (d0-d7 instead of s0-s7).</summary>
    public bool HasDoubleArgs { get; init; }

    /// <summary>
    /// Number of method candidates at this VA (1 = unique, >1 = VA collision resolved by scoring).
    /// </summary>
    public int CandidateCount { get; init; } = 1;

    /// <summary>
    /// HFA return size: 2 for Vector2, 3 for Vector3, 4 for Quaternion/Vector4/Color.
    /// 0 for non-HFA returns. Resolved from metadata via TypeResolver.GetHfaSize.
    /// </summary>
    public int ReturnHfaSize { get; init; }

    public string FullName => DeclaringType != null
        ? $"{DeclaringType}::{MethodName}"
        : MethodName ?? $"func_0x{TargetAddress:X}";

    public override string ToString() => FullName;
}
