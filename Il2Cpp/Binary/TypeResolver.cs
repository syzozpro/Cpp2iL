using System.Collections.Concurrent;
using Rosetta.Common;
using Rosetta.Metadata;
using Rosetta.Pipeline;

namespace Rosetta.Binary;

/// <summary>
/// Resolves Il2CppType indices to human-readable C# type names.
///
/// Source Path: il2cpp-runtime-metadata.h lines 54-76 (Il2CppType struct)
/// Source Path: il2cpp-blob.h lines 6-47 (Il2CppTypeEnum values)
///
/// Mapping Logic:
///   - For primitive types (VOID, BOOLEAN, I4, STRING, etc.): 
///     return the C# keyword directly from Il2CppTypeEnum.
///   - For CLASS/VALUETYPE: data = TypeDefinitionIndex, look up the
///     TypeDefinition name from metadata.
///   - For SZARRAY: data = pointer to element Il2CppType, recursively
///     resolve and append "[]".
///   - For GENERICINST: data = pointer to Il2CppGenericClass, resolve
///     the base type (simplified without full generic args for now).
///   - For VAR/MVAR: data = GenericParameterIndex.
/// </summary>
public sealed class TypeResolver
{
    private readonly Il2CppType[] _types;
    private readonly MetadataParser _metadata;
    private readonly RegistrationResolver _registration;
    private readonly string?[] _resolvedCache;
    private readonly string?[] _genericInstArgsCache;
    private readonly bool[] _genericInstArgsEvaluated;

    public TypeResolver(MetadataParser metadata, RegistrationResolver registration)
    {
        _metadata = metadata;
        _registration = registration;
        _types = registration.Types;

        _resolvedCache = new string?[_types.Length];
        _genericInstArgsCache = new string?[registration.GenericInstVAs.Length];
        _genericInstArgsEvaluated = new bool[registration.GenericInstVAs.Length];
    }

    /// <summary>
    /// Resolve a TypeIndex (used in MethodDefinition.ReturnType, etc.)
    /// to a C# type name string.
    ///
    /// Source: MethodDefinition.ReturnType is an index into the
    /// g_Il2CppTypeTable (MetadataRegistration.types array).
    /// Source: WriteIl2CppTypeDefinitions.cs line 40 — Types.SortedItems
    /// </summary>
    public string ResolveTypeName(int typeIndex)
    {
        if (typeIndex < 0 || typeIndex >= _types.Length)
            return $"Type_{typeIndex}";

        if (_resolvedCache[typeIndex] == null)
        {
            _resolvedCache[typeIndex] = ResolveType(_types[typeIndex]);
        }
        return _resolvedCache[typeIndex]!;
    }

    /// <summary>
    /// Get the raw Il2CppType for a given type index.
    /// Returns null if index is out of range.
    /// </summary>
    public Il2CppType? GetTypeByIndex(int typeIndex)
    {
        if (typeIndex < 0 || typeIndex >= _types.Length)
            return null;
        return _types[typeIndex];
    }

    /// <summary>
    /// Resolve an Il2CppType struct to a C# type name.
    ///
    /// Source: il2cpp-runtime-metadata.h lines 54-76
    ///   The type tag determines how to interpret the data union.
    /// </summary>
    private string ResolveType(in Il2CppType type)
    {
        // Check for primitive C# keyword first
        string? keyword = type.TypeEnum.ToCSharpKeyword();
        if (keyword != null)
        {
            if (type.ByRef)
            {
                if ((type.Attrs & 0x0002) != 0) return $"out {keyword}";
                if ((type.Attrs & 0x0001) != 0) return $"in {keyword}";
                return $"ref {keyword}";
            }
            return keyword;
        }

        string result = type.TypeEnum switch
        {
            // CLASS and VALUETYPE: data = TypeDefinitionIndex
            // Source: il2cpp-runtime-metadata.h line 60
            Il2CppTypeEnum.IL2CPP_TYPE_CLASS or
            Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE =>
                ResolveClassOrValueType(type.KlassIndex),

            // SZARRAY: data = pointer to element Il2CppType
            // Source: il2cpp-runtime-metadata.h line 62
            Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY =>
                ResolveSzArray(type.DataPointer),

            // GENERICINST: data = pointer to Il2CppGenericClass
            // Source: il2cpp-runtime-metadata.h line 67
            Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST =>
                ResolveGenericInst(type.DataPointer),

            // VAR: generic type parameter (T in class Foo<T>)
            // Source: il2cpp-runtime-metadata.h line 65
            Il2CppTypeEnum.IL2CPP_TYPE_VAR =>
                ResolveGenericParameter(type.GenericParameterIndex),

            // MVAR: generic method parameter (T in void Bar<T>())
            // Source: il2cpp-runtime-metadata.h line 66
            Il2CppTypeEnum.IL2CPP_TYPE_MVAR =>
                ResolveGenericParameter(type.GenericParameterIndex),

            // PTR: pointer to another type
            Il2CppTypeEnum.IL2CPP_TYPE_PTR =>
                ResolvePtrType(type.DataPointer),

            // ENUM: same layout as VALUETYPE
            Il2CppTypeEnum.IL2CPP_TYPE_ENUM =>
                ResolveClassOrValueType(type.KlassIndex),

            // ARRAY: multi-dimensional array
            // Source: il2cpp-runtime-metadata.h lines 11-19, 63:
            //   Il2CppArrayType *array; /* for ARRAY */
            //   struct Il2CppArrayType { const Il2CppType* etype; uint8_t rank; ... };
            Il2CppTypeEnum.IL2CPP_TYPE_ARRAY =>
                ResolveArray(type.DataPointer),

            // Fallback
            _ => type.TypeEnum.ToString(),
        };

        if (type.ByRef)
        {
            if ((type.Attrs & 0x0002) != 0) return $"out {result}";
            if ((type.Attrs & 0x0001) != 0) return $"in {result}";
            return $"ref {result}";
        }
        return result;
    }

    /// <summary>
    /// Resolve CLASS/VALUETYPE/ENUM via TypeDefinitionIndex.
    /// Source: il2cpp-runtime-metadata.h line 60:
    ///   TypeDefinitionIndex __klassIndex;
    /// </summary>
    private string ResolveClassOrValueType(int klassIndex)
    {
        if (klassIndex < 0 || klassIndex >= _metadata.TypeDefinitions.Length)
            return $"TypeDef_{klassIndex}";

        var td = _metadata.TypeDefinitions[klassIndex];
        return td.FullName;
    }

    /// <summary>
    /// Resolve SZARRAY by looking up the element type pointer.
    /// Source: il2cpp-runtime-metadata.h line 62:
    ///   const Il2CppType *type;  // for PTR and SZARRAY
    /// The data field is a VA pointer to another Il2CppType.
    /// </summary>
    private string ResolveSzArray(ulong elementTypeVA)
    {
        var elementType = ReadTypeAtVA(elementTypeVA);
        if (elementType == null)
            return "?[]";

        return ResolveType(elementType.Value) + "[]";
    }

    /// <summary>
    /// Resolve PTR type.
    /// Source: il2cpp-runtime-metadata.h line 62
    /// </summary>
    private string ResolvePtrType(ulong pointedTypeVA)
    {
        var pointedType = ReadTypeAtVA(pointedTypeVA);
        if (pointedType == null)
            return "void*";

        return ResolveType(pointedType.Value) + "*";
    }

    /// <summary>
    /// Resolve ARRAY (multi-dimensional array).
    ///
    /// Source Path: il2cpp-runtime-metadata.h lines 11-19:
    ///   typedef struct Il2CppArrayType {
    ///     const Il2CppType* etype;     // +0: element type pointer (8 bytes)
    ///     uint8_t rank;                // +8: number of dimensions
    ///     uint8_t numsizes;            // +9
    ///     uint8_t numlobounds;         // +10
    ///     int *sizes;                  // +16 (aligned)
    ///     int *lobounds;               // +24
    ///   } Il2CppArrayType;
    ///
    /// Source Path: il2cpp-runtime-metadata.h line 63:
    ///   Il2CppArrayType *array; /* for ARRAY */
    ///
    /// Mapping Logic: Follow the Il2CppArrayType pointer, read etype at +0
    /// (with relocation), read rank at +8, format as "elementType[,,,]".
    /// </summary>
    private string ResolveArray(ulong arrayTypeVA)
    {
        if (arrayTypeVA == 0)
            return "?[,]";

        long fileOff = _registration.ElfParser.VirtualToFileOffset(arrayTypeVA);
        int ptrSize = _registration.ElfParser.PointerSize;
        if (fileOff < 0 || fileOff + ptrSize + 1 > _registration.ElfData.Length)
            return "?[,]";

        // Read etype pointer at +0 (needs relocation)
        ulong etypeVA = _registration.ReadRelocatedPointer(arrayTypeVA);

        // Read rank at +ptrSize (after the etype pointer)
        byte rank = _registration.ElfData.Span[(int)(fileOff + ptrSize)];

        // Resolve element type
        string elementName = "?";
        if (etypeVA != 0)
        {
            var elementType = ReadTypeAtVA(etypeVA);
            if (elementType != null)
                elementName = ResolveType(elementType.Value);
        }

        // Format: rank 2 → "int[,]", rank 3 → "int[,,]", etc.
        string commas = rank > 1 ? new string(',', rank - 1) : "";
        return $"{elementName}[{commas}]";
    }

    /// <summary>
    /// Resolve GENERICINST. The data is a pointer to Il2CppGenericClass.
    ///
    /// Source: il2cpp-runtime-metadata.h lines 35-40:
    ///   typedef struct Il2CppGenericClass {
    ///     const Il2CppType* type;        // +0: the generic type def
    ///     Il2CppGenericContext context;   // +8: { class_inst(8), method_inst(8) }
    ///     Il2CppClass *cached_class;     // +24
    ///   } Il2CppGenericClass;
    ///
    /// We read +0 to get the open generic type, then resolve its name.
    /// For the generic arguments, we read the Il2CppGenericInst at context.class_inst.
    ///
    /// Source: il2cpp-runtime-metadata.h lines 21-25:
    ///   typedef struct Il2CppGenericInst {
    ///     uint32_t type_argc;        // +0
    ///     const Il2CppType **type_argv; // +8
    ///   } Il2CppGenericInst;
    /// </summary>
    private string ResolveGenericInst(ulong genericClassVA)
    {
        // Read the base type pointer (or index) from Il2CppGenericClass+0
        ulong baseTypeVA = _registration.ReadRelocatedPointer(genericClassVA);
        if (baseTypeVA == 0)
            return "GenericInst_?";

        string baseName;
        if (_metadata.EffectiveVersion < 27)
        {
            int typeDefIdx = (int)baseTypeVA;
            if (typeDefIdx >= 0 && typeDefIdx < _metadata.TypeDefinitions.Length)
            {
                baseName = _metadata.TypeDefinitions[typeDefIdx].FullName;
            }
            else
            {
                baseName = $"TypeDef_{typeDefIdx}";
            }
        }
        else
        {
            var baseType = ReadTypeAtVA(baseTypeVA);
            baseName = baseType != null ? ResolveType(baseType.Value) : "?";
        }

        // Read Il2CppGenericContext.class_inst at +ptrSize
        //   Il2CppGenericClass: { Il2CppType* type(ptr), Il2CppGenericContext context(2*ptr), ... }
        //   class_inst is the first field of Il2CppGenericContext at offset ptrSize
        int ptrSize = _registration.ElfParser.PointerSize;
        ulong classInstVA = _registration.ReadRelocatedPointer(genericClassVA + (ulong)ptrSize);
        if (classInstVA == 0)
            return baseName + "<>";

        // Read type_argc at +0
        long classInstFileOff = _registration.ElfParser.VirtualToFileOffset(classInstVA);
        if (classInstFileOff <= 0)
            return baseName + "<>";

        uint typeArgc = BitConverter.ToUInt32(
            _registration.ElfData.Span.Slice((int)classInstFileOff, 4));

        // Read type_argv ptr at +ptrSize (after type_argc which is padded to ptrSize on 64-bit)
        //   Il2CppGenericInst: { uint32_t type_argc(+0), [pad], Il2CppType** type_argv(+ptrSize) }
        //   On 32-bit: type_argv at +4 (no padding)
        //   On 64-bit: type_argv at +8 (4 bytes padding after uint32)
        ulong typeArgvVA = _registration.ReadRelocatedPointer(classInstVA + (ulong)ptrSize);
        if (typeArgvVA == 0 || typeArgc == 0)
            return baseName + "<>";

        // Resolve each generic argument
        var argNames = new string[typeArgc];
        for (uint i = 0; i < typeArgc; i++)
        {
            ulong argEntryVA = typeArgvVA + i * (ulong)ptrSize;
            ulong argTypeVA = _registration.ReadRelocatedPointer(argEntryVA);
            var argType = (argTypeVA != 0) ? ReadTypeAtVA(argTypeVA) : null;
            argNames[i] = argType != null ? ResolveType(argType.Value) : "?";
        }

        // Strip backtick-arity from the base type name.
        // Source Path: NamingExtensions.cs lines 60-68:
        //   int num = typeName.IndexOf('`');
        //   if (num != -1) typeName = typeName.Substring(0, num);
        //
        // Mapping Logic: The metadata stores generic types as "List`1", "Dictionary`2", etc.
        //   When we append explicit <T> args, the arity suffix must be removed.
        //
        // For nested generics like "Outer`1+Inner`1", we must strip ONLY the last
        // backtick+arity to avoid corrupting the outer type name. Then replace the
        // IL '+' nesting separator with C#'s '.'.
        baseName = Rosetta.Analysis.Utils.TypeUtils.StripAritySuffix(baseName);

        // Replace IL nested type separator '+' with C# '.'
        baseName = baseName.Replace('+', '.');

        return $"{baseName}<{string.Join(", ", argNames)}>";
    }

    /// <summary>
    /// Resolve a GenericInst table index to a formatted generic args string.
    ///
    /// This is used for MethodSpec.MethodInstIndex and MethodSpec.ClassInstIndex
    /// to resolve the actual type arguments of generic method instantiations.
    ///
    /// Example: instIndex for GetComponent&lt;AudioSource&gt;() → "&lt;AudioSource&gt;"
    /// Example: instIndex for Dictionary&lt;string, int&gt; → "&lt;string, int&gt;"
    ///
    /// Source: RegistrationResolver.GenericInstVAs[instIndex] → VA of Il2CppGenericInst
    /// Source: il2cpp-runtime-metadata.h lines 21-25:
    ///   typedef struct Il2CppGenericInst {
    ///     uint32_t type_argc;          // +0
    ///     const Il2CppType **type_argv; // +8
    ///   } Il2CppGenericInst;
    /// </summary>
    /// <returns>Formatted string like "&lt;AudioSource&gt;" or null if unresolvable.</returns>
    public string? ResolveGenericInstArgs(int instIndex)
    {
        if (instIndex < 0 || instIndex >= _genericInstArgsCache.Length)
            return null;
        if (!_genericInstArgsEvaluated[instIndex])
        {
            _genericInstArgsCache[instIndex] = ResolveGenericInstArgsInternal(instIndex);
            _genericInstArgsEvaluated[instIndex] = true;
        }
        return _genericInstArgsCache[instIndex];
    }

    private string? ResolveGenericInstArgsInternal(int instIndex)
    {
        var genericInstVAs = _registration.GenericInstVAs;
        if (instIndex < 0 || instIndex >= genericInstVAs.Length)
            return null;

        ulong instVA = genericInstVAs[instIndex];
        if (instVA == 0)
            return null;

        // Read type_argc at +0
        long fileOff = _registration.ElfParser.VirtualToFileOffset(instVA);
        if (fileOff <= 0)
            return null;

        uint typeArgc = BitConverter.ToUInt32(
            _registration.ElfData.Span.Slice((int)fileOff, 4));
        if (typeArgc == 0 || typeArgc > 16) // sanity check
            return null;

        // Read type_argv pointer at +ptrSize
        //   Il2CppGenericInst: { uint32_t type_argc(+0), [pad], Il2CppType** type_argv(+ptrSize) }
        int ptrSize = _registration.ElfParser.PointerSize;
        ulong typeArgvVA = _registration.ReadRelocatedPointer(instVA + (ulong)ptrSize);
        if (typeArgvVA == 0)
            return null;

        // Resolve each type argument
        var argNames = new string[typeArgc];
        for (uint i = 0; i < typeArgc; i++)
        {
            ulong argEntryVA = typeArgvVA + i * (ulong)ptrSize;
            ulong argTypeVA = _registration.ReadRelocatedPointer(argEntryVA);
            var argType = (argTypeVA != 0) ? ReadTypeAtVA(argTypeVA) : null;
            argNames[i] = argType != null ? ResolveType(argType.Value) : "?";
        }

        return $"<{string.Join(", ", argNames)}>";
    }

    /// <summary>
    /// Retrieve the concrete type arguments of a generic instantiation by its index.
    /// </summary>
    public Il2CppType[]? GetGenericInstTypes(int instIndex)
    {
        var genericInstVAs = _registration.GenericInstVAs;
        if (instIndex < 0 || instIndex >= genericInstVAs.Length)
            return null;

        ulong instVA = genericInstVAs[instIndex];
        if (instVA == 0)
            return null;

        long fileOff = _registration.ElfParser.VirtualToFileOffset(instVA);
        if (fileOff <= 0)
            return null;

        uint typeArgc = BitConverter.ToUInt32(
            _registration.ElfData.Span.Slice((int)fileOff, 4));
        if (typeArgc == 0 || typeArgc > 16)
            return null;

        int ptrSize = _registration.ElfParser.PointerSize;
        ulong typeArgvVA = _registration.ReadRelocatedPointer(instVA + (ulong)ptrSize);
        if (typeArgvVA == 0)
            return null;

        var types = new Il2CppType[typeArgc];
        for (uint i = 0; i < typeArgc; i++)
        {
            ulong argEntryVA = typeArgvVA + i * (ulong)ptrSize;
            ulong argTypeVA = _registration.ReadRelocatedPointer(argEntryVA);
            var argType = (argTypeVA != 0) ? ReadTypeAtVA(argTypeVA) : null;
            if (argType == null) return null;
            types[i] = argType.Value;
        }

        return types;
    }

    /// <summary>
    /// Recursively resolve generic type placeholders (VAR/MVAR) to their concrete arguments.
    /// </summary>
    public Il2CppType ResolveGenericType(Il2CppType type, Il2CppType[]? classInstTypes, Il2CppType[]? methodInstTypes)
    {
        if (type.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_VAR)
        {
            int index = type.GenericParameterIndex;
            if (index >= 0 && index < _metadata.GenericParameters.Length)
            {
                var gp = _metadata.GenericParameters[index];
                if (classInstTypes != null && gp.Num >= 0 && gp.Num < classInstTypes.Length)
                {
                    return ResolveGenericType(classInstTypes[gp.Num], classInstTypes, methodInstTypes);
                }
            }
        }
        else if (type.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_MVAR)
        {
            int index = type.GenericParameterIndex;
            if (index >= 0 && index < _metadata.GenericParameters.Length)
            {
                var gp = _metadata.GenericParameters[index];
                if (methodInstTypes != null && gp.Num >= 0 && gp.Num < methodInstTypes.Length)
                {
                    return ResolveGenericType(methodInstTypes[gp.Num], classInstTypes, methodInstTypes);
                }
            }
        }
        return type;
    }

    /// <summary>
    /// Resolve generic parameter by index.
    ///
    /// Source: GenericParameters section (Section 12)
    ///   Il2CppGenericParameter.NameIndex → MetadataStrings → "T", "TKey", etc.
    /// </summary>
    private string ResolveGenericParameter(int index)
    {
        if (index >= 0 && index < _metadata.GenericParameters.Length)
        {
            string? name = _metadata.GenericParameters[index].Name;
            if (!string.IsNullOrEmpty(name))
                return name;
        }

        // Fallback to positional names
        if (index < 26)
            return $"{(char)('T' + (index % 26))}";
        return $"T{index}";
    }

    /// <summary>
    /// Read an Il2CppType from a VA in the binary.
    /// Used for following pointer-based type references (SZARRAY, PTR, GENERICINST, ARRAY).
    ///
    /// Source Path: il2cpp-runtime-metadata.h lines 56-68 (data union)
    ///   - PTR/SZARRAY: data = const Il2CppType *type (pointer, needs relocation)
    ///   - ARRAY: data = Il2CppArrayType *array (pointer, needs relocation)
    ///   - GENERICINST: data = Il2CppGenericClass *generic_class (pointer, needs relocation)
    ///   - CLASS/VALUETYPE: data = TypeDefinitionIndex (int32, no relocation)
    ///   - VAR/MVAR: data = GenericParameterIndex (int32, no relocation)
    ///
    /// Mapping Logic: The Data field at offset +0 of Il2CppType is a union.
    ///   For pointer-based types, the on-disk value is zero (RELA relocation target).
    ///   We must resolve it through the relocation map, matching the same fixup
    ///   applied during type table parsing (RegistrationResolver line 677-685).
    /// </summary>
    private Il2CppType? ReadTypeAtVA(ulong va)
    {
        if (va == 0) return null;

        long fileOff = _registration.ElfParser.VirtualToFileOffset(va);
        int typeSizeOf = Il2CppType.GetSizeOf(_registration.ElfParser.Is32Bit);
        if (fileOff < 0 || fileOff + typeSizeOf > _registration.ElfData.Length)
            return null;

        Span<byte> buf = stackalloc byte[typeSizeOf];
        _registration.ElfData.Span.Slice((int)fileOff, typeSizeOf).CopyTo(buf);
        var parsed = Il2CppType.Parse(buf, _registration.ElfParser.Is32Bit);

        // Apply relocation for pointer-based data fields.
        // Source: il2cpp-runtime-metadata.h lines 62-67:
        //   const Il2CppType *type;        // PTR, SZARRAY
        //   Il2CppArrayType *array;        // ARRAY
        //   Il2CppGenericClass *generic_class; // GENERICINST
        // These are pointers that are zero on-disk due to R_AARCH64_RELATIVE relocations.
        if (parsed.TypeEnum is
            Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY or
            Il2CppTypeEnum.IL2CPP_TYPE_PTR or
            Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST or
            Il2CppTypeEnum.IL2CPP_TYPE_ARRAY)
        {
            ulong resolvedData = _registration.ReadRelocatedPointer(va);
            parsed = parsed with { Data = resolvedData };
        }

        return parsed;
    }

    /// <summary>
    /// Computes the HFA (Homogeneous Floating-point Aggregate) size for a given type.
    /// Returns the number of registers (1-4) if it's an HFA, or 0 if it's not.
    /// </summary>
    public int GetHfaSize(int typeIndex)
    {
        var type = GetTypeByIndex(typeIndex);
        if (type == null) return 0;
        return GetHfaSize(type.Value);
    }

    public int GetHfaSize(in Il2CppType type)
    {
        if (type.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_R4 || type.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_R8)
            return 1;
            
        if (type.TypeEnum != Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
            return 0; // Only structs can be HFAs

        var (size, _) = GetStructHfaInfo(type.KlassIndex);
        return size;
    }

    private readonly ConcurrentDictionary<int, (int Size, Il2CppTypeEnum ElementType)> _hfaCache = new();

    private (int Size, Il2CppTypeEnum ElementType) GetStructHfaInfo(int typeDefIndex)
    {
        if (typeDefIndex < 0 || typeDefIndex >= _metadata.TypeDefinitions.Length)
            return (0, Il2CppTypeEnum.IL2CPP_TYPE_END);

        if (_hfaCache.TryGetValue(typeDefIndex, out var cached))
            return cached;

        // Prevent infinite recursion for malformed self-referential structs
        _hfaCache[typeDefIndex] = (0, Il2CppTypeEnum.IL2CPP_TYPE_END);

        var td = _metadata.TypeDefinitions[typeDefIndex];
        
        int totalSize = 0;
        Il2CppTypeEnum hfaElementType = Il2CppTypeEnum.IL2CPP_TYPE_END;

        for (int i = 0; i < td.FieldCount; i++)
        {
            int fieldIdx = td.FieldStart + i;
            if (fieldIdx < 0 || fieldIdx >= _metadata.FieldDefinitions.Length)
                return (0, Il2CppTypeEnum.IL2CPP_TYPE_END);
            
            var fd = _metadata.FieldDefinitions[fieldIdx];
            
            // Ignore static fields!
            if (fd.IsStatic)
                continue;

            var fieldType = GetTypeByIndex(fd.TypeIndex);
            if (fieldType == null) return (0, Il2CppTypeEnum.IL2CPP_TYPE_END);

            if (fieldType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_R4 || 
                fieldType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_R8)
            {
                if (hfaElementType == Il2CppTypeEnum.IL2CPP_TYPE_END)
                    hfaElementType = fieldType.Value.TypeEnum;
                else if (hfaElementType != fieldType.Value.TypeEnum)
                    return (0, Il2CppTypeEnum.IL2CPP_TYPE_END); // mixed element types
                
                totalSize++;
            }
            else if (fieldType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
            {
                var (nestedSize, nestedType) = GetStructHfaInfo(fieldType.Value.KlassIndex);
                if (nestedSize == 0) return (0, Il2CppTypeEnum.IL2CPP_TYPE_END); // nested struct is not an HFA
                
                if (hfaElementType == Il2CppTypeEnum.IL2CPP_TYPE_END)
                    hfaElementType = nestedType;
                else if (hfaElementType != nestedType)
                    return (0, Il2CppTypeEnum.IL2CPP_TYPE_END); // nested struct has different HFA element type
                    
                totalSize += nestedSize;
            }
            else
            {
                return (0, Il2CppTypeEnum.IL2CPP_TYPE_END); // field is not a float/double or struct
            }
            
            if (totalSize > 4) return (0, Il2CppTypeEnum.IL2CPP_TYPE_END); // HFA cannot exceed 4 registers
        }
        
        var result = totalSize > 0 ? (totalSize, hfaElementType) : (0, Il2CppTypeEnum.IL2CPP_TYPE_END);
        _hfaCache[typeDefIndex] = result;
        return result;
    }

    /// <summary>
    /// Returns the instance field names for an HFA struct from metadata.
    /// Returns null if typeIndex is not an HFA struct.
    /// </summary>
    public string[]? GetHfaFieldNames(int typeIndex)
    {
        var type = GetTypeByIndex(typeIndex);
        if (type == null || type.Value.TypeEnum != Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE)
            return null;

        int typeDefIndex = type.Value.KlassIndex;
        if (typeDefIndex < 0 || typeDefIndex >= _metadata.TypeDefinitions.Length)
            return null;

        var td = _metadata.TypeDefinitions[typeDefIndex];
        var names = new List<string>();

        for (int i = 0; i < td.FieldCount; i++)
        {
            int fieldIdx = td.FieldStart + i;
            if (fieldIdx < 0 || fieldIdx >= _metadata.FieldDefinitions.Length)
                return null;

            var fd = _metadata.FieldDefinitions[fieldIdx];
            if (fd.IsStatic) continue;
            names.Add(fd.Name ?? $"field_{i}");
        }

        return names.Count >= 2 ? names.ToArray() : null;
    }
}
