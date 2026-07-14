using Rosetta.Common;
using Rosetta.IO;
using Rosetta.Pipeline;

namespace Rosetta.Binary;

/// <summary>
/// Locates the Il2CppCodeRegistration and Il2CppMetadataRegistration
/// structures within libil2cpp.so, then extracts the per-module
/// method pointer arrays.
///
/// Supports both 32-bit and 64-bit ELF binaries.
///
/// CRITICAL: On Android, all const struct pointers in .data.rel.ro
/// are ZERO on disk. They get filled in at load time via relocations:
///   ARM64: R_AARCH64_RELATIVE (0x403) in .rela.dyn
///   ARM32: R_ARM_RELATIVE (0x17) in .rel.dyn
///
/// Source of Truth:
///   - Struct layouts:        il2cpp-class-internals.h lines 585-647
///   - Registration emission: CodeRegistrationWriter.cs lines 104-123
///   - Per-module emission:   PerAssemblyCodeMetadataWriter.cs lines 80-99
/// </summary>
public sealed class RegistrationResolver
{
    private readonly EndianBinaryReader _reader;
    private readonly IBinaryParser _elf;

    // ========================================================================
    // Architecture-aware constants
    // ========================================================================
    private readonly bool _is32Bit;
    private readonly int _ptrSize;       // 4 or 8

    // Relocation type for RELATIVE relocations:
    //   ARM64: R_AARCH64_RELATIVE = 0x403
    //   ARM32: R_ARM_RELATIVE     = 0x17
    //   x86:   R_386_RELATIVE     = 0x08
    //   x64:   R_X86_64_RELATIVE  = 0x08
    private const uint R_AARCH64_RELATIVE = 0x403;
    private const uint R_ARM_RELATIVE     = 0x17;
    private const uint R_386_RELATIVE     = 0x08;
    private const uint R_X86_64_RELATIVE  = 0x08;

    // ========================================================================
    // Struct offsets — computed dynamically based on pointer size.
    //
    // On 64-bit: uint32 + [pad4] + ptr = 4+4+8 = 16 per count/ptr pair
    //                     uint32 naturally aligned to 4, pointer to 8.
    // On 32-bit: uint32 + ptr = 4+4 = 8 per count/ptr pair (no padding)
    //
    // Source: il2cpp-class-internals.h lines 606-647
    // ========================================================================

    // Il2CppCodeRegistration offsets
    private readonly int CodeReg_codeGenModulesCount;
    private readonly int CodeReg_codeGenModulesPtr;
    private readonly int CodeReg_genericMethodPointersCount;
    private readonly int CodeReg_genericMethodPointersPtr;

    // Il2CppCodeGenModule offsets
    //   +0: const char* moduleName
    //   +ptrSize: uint32_t methodPointerCount
    //   +ptrSize+4 (+ optional pad): const Il2CppMethodPointer* methodPointers
    private readonly int CGM_moduleName;
    private readonly int CGM_methodPointerCount;
    private readonly int CGM_methodPointers;

    // Il2CppMetadataRegistration offsets
    private readonly int MetaReg_typesCount;
    private readonly int MetaReg_typesPtr;
    private readonly int MetaReg_genericInstsCount;
    private readonly int MetaReg_genericInstsPtr;
    private readonly int MetaReg_genericMethodTableCount;
    private readonly int MetaReg_genericMethodTablePtr;
    private readonly int MetaReg_methodSpecsCount;
    private readonly int MetaReg_methodSpecsPtr;
    private readonly int MetaReg_fieldOffsetsCount;
    private readonly int MetaReg_fieldOffsetsPtr;
    private readonly int MetaReg_typeDefSizesCount;
    private readonly int MetaReg_typeDefSizesPtr;
    private readonly int MetaReg_metadataUsagesCount;
    private readonly int MetaReg_metadataUsagesPtr;
    private readonly int MetaReg_Size;

    // ========================================================================

    // Resolved data
    public ulong CodeRegistrationVA { get; private set; }
    public ulong MetadataRegistrationVA { get; private set; }
    public int CodeGenModulesCount { get; private set; }

    /// <summary>Per-module method pointers: moduleName → pointer array.</summary>
    public Dictionary<string, ulong[]> ModuleMethodPointers { get; } = new();

    /// <summary>Flat array of all method pointers across all modules, ordered by module.</summary>
    public ulong[] AllMethodPointers { get; private set; } = [];

    /// <summary>Number of Il2CppType entries in the types table.</summary>
    public int TypesCount { get; private set; }

    /// <summary>
    /// Parsed Il2CppType table. Index = TypeIndex used in metadata.
    /// Source: WriteIl2CppTypeDefinitions.cs — g_Il2CppTypeTable.
    /// </summary>
    public Il2CppType[] Types { get; private set; } = [];

    /// <summary>
    /// Generic method pointer table from Il2CppCodeRegistration.genericMethodPointers.
    /// Index = pointerTableIndex from Il2CppGenericMethodFunctionsDefinitions.
    /// Source: CodeRegistrationWriter.cs line 108-109
    /// </summary>
    public ulong[] GenericMethodPointers { get; private set; } = [];

    /// <summary>
    /// Il2CppGenericMethodFunctionsDefinitions table.
    /// Each entry maps a MethodSpec index to a pointer table index.
    ///
    /// Source: WriteIl2CppGenericMethodTable.cs line 20:
    ///   { genericMethodIndex, pointerTableIndex, invokerIndex, adjustorThunkTableIndex }
    ///   = 4 × int32 = 16 bytes
    /// </summary>
    public GenericMethodFuncDef[] GenericMethodTable { get; private set; } = [];

    /// <summary>
    /// Il2CppMethodSpec table. Each entry = { methodDefIndex, classInstIndex, methodInstIndex }.
    ///
    /// Source: WriteIl2CppGenericMethodDefinitions.cs lines 47-53:
    ///   { methodDefinitionIndex(int32), classIndexIndex(int32), methodIndexIndex(int32) }
    ///   = 3 × int32 = 12 bytes
    /// </summary>
    public MethodSpecDef[] MethodSpecs { get; private set; } = [];

    /// <summary>
    /// Il2CppGenericInst pointer table. Each entry is the VA of an Il2CppGenericInst struct.
    /// Indexed by MethodSpec.ClassInstIndex and MethodSpec.MethodInstIndex.
    ///
    /// Source: il2cpp-class-internals.h lines 628-630:
    ///   int32_t genericInstsCount;         // +16
    ///   Il2CppGenericInst** genericInsts;   // +24
    ///
    /// Each Il2CppGenericInst = { uint32_t type_argc(+0), Il2CppType** type_argv(+8) }
    /// </summary>
    public ulong[] GenericInstVAs { get; private set; } = [];

    /// <summary>
    /// Field offsets table from Il2CppMetadataRegistration.fieldOffsets.
    /// Outer array index = TypeDefinition index.
    /// Inner array = per-field byte offset (int32) for that type's fields.
    /// null entry means the type had no resolvable field offsets pointer.
    ///
    /// Source: il2cpp-class-internals.h:640-641:
    ///   FieldIndex fieldOffsetsCount;
    ///   const int32_t** fieldOffsets;
    ///
    /// Source: GlobalMetadata.cpp:1097-1103:
    ///   int32_t offset = s_Il2CppMetadataRegistration->fieldOffsets[typeDefIndex][fieldIndexInType];
    /// </summary>
    public int[]?[] FieldOffsets { get; private set; } = [];

    /// <summary>
    /// Type definition sizes from Il2CppMetadataRegistration.typeDefSizes.
    /// Index = TypeDefinition index. Contains instance size, native size, etc.
    /// </summary>
    public Il2CppTypeDefinitionSizes[] TypeDefSizes { get; private set; } = [];

    /// <summary>
    /// V24 metadataUsages: maps usage index → .bss virtual address.
    /// Built from MetadataRegistration.metadataUsages pointer array.
    /// Empty for V27+ (table was removed).
    /// </summary>
    public ulong[] MetadataUsageAddresses { get; private set; } = [];

    public RegistrationResolver(ReadOnlyMemory<byte> elfData, IBinaryParser elf)
    {
        _reader = new EndianBinaryReader(elfData);
        _elf = elf;
        _is32Bit = elf.Is32Bit;
        _ptrSize = elf.PointerSize;

        // Compute struct offsets based on pointer size.
        // On 64-bit: each {uint32 count, ptr} pair occupies 16 bytes (4 + 4pad + 8).
        // On 32-bit: each {uint32 count, ptr} pair occupies 8 bytes (4 + 4, no padding).
        //
        // Il2CppCodeRegistration layout:
        //   Field 0: reversePInvokeWrapperCount(4) + pad? + reversePInvokeWrappers(ptr)
        //   Field 1: genericMethodPointersCount(4) + pad? + genericMethodPointers(ptr)
        //   Field 2: genericAdjustorThunks(ptr) — just a pointer, no count
        //   Field 3: invokerPointersCount(4) + pad? + invokerPointers(ptr)
        //   Field 4: unresolvedIndirectCallCount(4) + pad? + 3× ptr
        //   Field 5: interopDataCount(4) + pad? + interopData(ptr)
        //   Field 6: windowsRuntimeFactoryCount(4) + pad? + windowsRuntimeFactoryTable(ptr)
        //   Field 7: codeGenModulesCount(4) + pad? + codeGenModules(ptr)
        int countPtrPairSize = _is32Bit ? 8 : 16;  // uint32 + [pad] + ptr

        // 64-bit offsets: 16, 24 / 120, 128
        // 32-bit offsets: 8, 12 / 60, 64
        CodeReg_genericMethodPointersCount = countPtrPairSize;           // after pair0
        CodeReg_genericMethodPointersPtr   = countPtrPairSize + 4 + (_is32Bit ? 0 : 4); // count + pad + ptr-start

        // For codeGenModules: it's pair 7 in sequence but we need to count the exact offset.
        // Layout: pair0(countPtrPairSize) + pair1(countPtrPairSize) + ptr(ptrSize) ← genericAdjustorThunks
        //       + pair2(countPtrPairSize) + pair3(countPtrPairSize ← count + 3*ptr)
        //       + pair4(countPtrPairSize) + pair5(countPtrPairSize) + pair6(countPtrPairSize)
        // Let's compute step by step:
        //   +0:   reversePInvokeWrapperCount(4) [+pad] reversePInvokeWrappers(ptr)
        //   +P:   genericMethodPointersCount(4) [+pad] genericMethodPointers(ptr)
        //   +2P:  genericAdjustorThunks(ptr)  — standalone ptr
        //   +2P+ptr: invokerPointersCount(4) [+pad] invokerPointers(ptr)
        //   +3P+ptr: unresolvedIndirectCallCount(4) [+pad] 3× ptr
        //   =(3P+ptr) + 4 [+pad] + 3*ptr
        //     64: (48+8) + 4+4 + 24 = 88
        //     32: (24+4) + 4   + 12 = 44
        //   +next: interopDataCount(4) [+pad] interopData(ptr) = P
        //   +next: windowsRuntimeFactoryCount(4) [+pad] windowsRuntimeFactoryTable(ptr) = P
        //   +next: codeGenModulesCount(4) [+pad] codeGenModules(ptr)
        //
        // Computing exactly:
        if (_is32Bit)
        {
            // 32-bit: no padding between uint32 and ptr (all 4-byte aligned)
            // +0:  count(4) + ptr(4) = 8    → reversePInvoke
            // +8:  count(4) + ptr(4) = 8    → genericMethodPointers
            // +16: ptr(4)                   → genericAdjustorThunks
            // +20: count(4) + ptr(4) = 8    → invokerPointers
            // +28: count(4) + 3*ptr(12)     → unresolvedIndirectCallCount + 3 ptrs
            // +44: count(4) + ptr(4) = 8    → interopData
            // +52: count(4) + ptr(4) = 8    → windowsRuntimeFactory
            // +60: count(4)                 → codeGenModulesCount
            // +64: ptr(4)                   → codeGenModules
            CodeReg_codeGenModulesCount = 60;
            CodeReg_codeGenModulesPtr   = 64;
        }
        else
        {
            // 64-bit: uint32 padded to 8-byte boundary before pointer
            CodeReg_codeGenModulesCount = 120;
            CodeReg_codeGenModulesPtr   = 128;
        }

        // Il2CppCodeGenModule: { ptr, uint32, [pad], ptr, ... }
        CGM_moduleName         = 0;
        CGM_methodPointerCount = _ptrSize;                               // after moduleName ptr
        CGM_methodPointers     = _ptrSize + 4 + (_is32Bit ? 0 : 4);     // count + pad + ptr

        // Il2CppMetadataRegistration — 8 count/ptr pairs
        // Each pair: int32(4) + [pad] + ptr = countPtrPairSize
        // Pair 0: genericClassesCount/genericClasses       (+0)
        // Pair 1: genericInstsCount/genericInsts            (+P)
        // Pair 2: genericMethodTableCount/genericMethodTable (+2P)
        // Pair 3: typesCount/types                          (+3P)
        // Pair 4: methodSpecsCount/methodSpecs              (+4P)
        // Pair 5: fieldOffsetsCount/fieldOffsets             (+5P)
        // Pair 6: typeDefinitionsSizesCount/typeDefSizes     (+6P)
        // Pair 7: metadataUsagesCount(size_t)/metadataUsages (+7P)
        int p = countPtrPairSize;
        MetaReg_genericInstsCount      = p;
        MetaReg_genericInstsPtr        = p + 4 + (_is32Bit ? 0 : 4);
        MetaReg_genericMethodTableCount = 2 * p;
        MetaReg_genericMethodTablePtr   = 2 * p + 4 + (_is32Bit ? 0 : 4);
        MetaReg_typesCount             = 3 * p;
        MetaReg_typesPtr               = 3 * p + 4 + (_is32Bit ? 0 : 4);
        MetaReg_methodSpecsCount       = 4 * p;
        MetaReg_methodSpecsPtr         = 4 * p + 4 + (_is32Bit ? 0 : 4);
        MetaReg_fieldOffsetsCount      = 5 * p;
        MetaReg_fieldOffsetsPtr        = 5 * p + 4 + (_is32Bit ? 0 : 4);
        MetaReg_fieldOffsetsPtr        = 5 * p + 4 + (_is32Bit ? 0 : 4);
        MetaReg_typeDefSizesCount      = 6 * p;
        MetaReg_typeDefSizesPtr        = 6 * p + 4 + (_is32Bit ? 0 : 4);

        // metadataUsagesCount is size_t (pointer-sized), not int32
        // So pair 7 starts at 7*p
        MetaReg_metadataUsagesCount    = 7 * p;
        MetaReg_metadataUsagesPtr      = 7 * p + _ptrSize;  // size_t(ptrSize) + ptr(ptrSize)
        MetaReg_Size                   = 7 * p + _ptrSize + _ptrSize;
    }

    /// <summary>Expose the ELF parser for VA-to-file translation.</summary>
    public IBinaryParser ElfParser => _elf;

    /// <summary>Expose the raw ELF data for reading type structs.</summary>
    public ReadOnlyMemory<byte> ElfData => _reader.Memory;

    /// <summary>
    /// Master resolve entry point.
    /// 1. Parse .rela.dyn to build the relocation map
    /// 2. Find .dll name strings in .rodata
    /// 3. Use relocations to find Il2CppCodeGenModule structs
    /// 4. Use relocations to find the g_CodeGenModules array
    /// 5. Use relocations to find Il2CppCodeRegistration
    /// 6. Extract all method pointers
    /// </summary>
    public bool Resolve(int expectedTypeDefCount = 0, int[]? fieldCounts = null)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"RegistrationResolver.Resolve(expectedTypeDefCount={expectedTypeDefCount})");
        if (_elf.RelocationMap.Count == 0)
        {
            Console.WriteLine("[REGISTRATION-WARN] No relocation entries found in ELF/PE binary. Ensure the binary is not stripped or obfuscated.");
            return false;
        }

        // Step 2: Find module structs by searching for .dll name relocations
        var moduleStructVAs = FindModuleStructs();
        if (moduleStructVAs.Count == 0)
        {
            Console.WriteLine("[REGISTRATION-WARN] No Il2CppCodeGenModule structures found. Couldn't find relocations pointing to DLL names.");
            return false;
        }

        // Step 3: Find the g_CodeGenModules pointer array
        ulong arrayVA = FindCodeGenModulesArray(moduleStructVAs);
        if (arrayVA == 0)
        {
            Console.WriteLine("[REGISTRATION-WARN] Could not locate the g_CodeGenModules pointer array in the binary.");
            return false;
        }

        // Step 4: Find CodeRegistration via a reloc pointing to the array
        FindCodeRegistration(arrayVA);
        if (CodeRegistrationVA == 0)
        {
            Console.WriteLine("[REGISTRATION-WARN] Could not locate CodeRegistration structure using g_CodeGenModules array.");
        }

        // Step 5: Parse all modules and extract method pointers
        bool ok = ParseAllModules(arrayVA, moduleStructVAs);
        if (!ok)
        {
            Console.WriteLine("[REGISTRATION-WARN] Parsing modules or extracting method pointers failed.");
        }

        // Step 6: Find MetadataRegistration and parse Il2CppType table
        if (expectedTypeDefCount > 0)
        {
            FindMetadataRegistration(expectedTypeDefCount);
            if (MetadataRegistrationVA != 0)
            {
                ParseTypeTable();
                ParseFieldOffsetsTable(expectedTypeDefCount, fieldCounts);
                ParseTypeDefSizesTable(expectedTypeDefCount);
                ParseMethodSpecTable();
                ParseGenericMethodTable();
                ParseGenericInstTable();
                ParseMetadataUsagesTable();
            }
            else
            {
                Console.WriteLine("[REGISTRATION-WARN] Could not locate MetadataRegistration structure in the binary.");
            }
        }

        // Step 7: Parse generic method pointers from CodeRegistration
        if (CodeRegistrationVA != 0)
        {
            ParseGenericMethodPointers();
        }

        return ok;
    }

    // ====================================================================

    /// <summary>
    /// Read a pointer value, resolving via relocation if the on-disk value is zero.
    /// Width-aware: reads 4 bytes on 32-bit, 8 bytes on 64-bit.
    /// </summary>
    public ulong ReadRelocatedPointer(ulong va)
    {
        if (_elf.RelocationMap.TryGetValue(va, out ulong resolved))
            return resolved;

        // Fall back to on-disk value
        long foff = _elf.VirtualToFileOffset(va);
        if (foff <= 0 || foff + _ptrSize > _reader.Length) return 0;

        _reader.Position = (int)foff;
        return _is32Bit ? _reader.ReadUInt32() : _reader.ReadUInt64();
    }

    /// <summary>
    /// Read a uint32 from a virtual address.
    /// </summary>
    public uint ReadUInt32(ulong va)
    {
        long fileOffset = _elf.VirtualToFileOffset(va);
        if (fileOffset <= 0 || fileOffset + 4 > _reader.Length) return 0;
        _reader.Position = (int)fileOffset;
        return _reader.ReadUInt32();
    }

    /// <summary>
    /// Read a uint32 from a file offset.
    /// </summary>
    private uint ReadUInt32At(long fileOffset)
    {
        if (fileOffset <= 0 || fileOffset + 4 > _reader.Length) return 0;
        _reader.Position = (int)fileOffset;
        return _reader.ReadUInt32();
    }

    /// <summary>
    /// Read a null-terminated string at a file offset.
    /// </summary>
    private string ReadStringAtFile(long fileOffset)
    {
        if (fileOffset <= 0 || fileOffset >= _reader.Length) return "";
        return _reader.ReadStringAt((int)fileOffset);
    }

    // ====================================================================
    // STEP 2: Find Il2CppCodeGenModule structs
    //
    // Source: PerAssemblyCodeMetadataWriter.cs line 80-99
    //   Il2CppCodeGenModule.moduleName (+0) is a pointer to a string
    //   that ends with ".dll" or is "__Generated".
    //
    // Strategy: Find all RELATIVE relocations whose addend points
    // to a .dll name string. The relocation's r_offset is the VA
    // of the moduleName field (+0), which IS the module struct VA.
    // ====================================================================
    private List<ulong> FindModuleStructs()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    RegistrationResolver.FindModuleStructs()");
        var moduleVAs = new List<ulong>();

        // Find .rodata boundaries
        var rodata = _elf.RoDataSection;
        if (rodata is null) return moduleVAs;

        int rodataFileStart = (int)rodata.Value.FileOffset;
        int rodataFileEnd = rodataFileStart + (int)rodata.Value.Size;

        // Find all .dll name string offsets in .rodata
        var dllNameVAs = new HashSet<ulong>();
        var span = _reader.Span;

        // Scan for ".dll\0" in .rodata
        byte[] pattern = [0x2E, 0x64, 0x6C, 0x6C, 0x00]; // ".dll\0"
        for (int i = rodataFileStart; i < rodataFileEnd - 5; i++)
        {
            if (span[i] == 0x2E && span[i + 1] == 0x64 && span[i + 2] == 0x6C &&
                span[i + 3] == 0x6C && span[i + 4] == 0x00)
            {
                // Walk back to find start of name string
                int start = i;
                while (start > rodataFileStart && span[start - 1] >= 32 && span[start - 1] < 127)
                    start--;

                string name = System.Text.Encoding.UTF8.GetString(span[start..(i + 4)]);
                if (name.Length >= 5 && !name.Contains('/'))
                {
                    ulong stringVa = rodata.Value.VirtualAddr + (ulong)(start - rodataFileStart);
                    dllNameVAs.Add(stringVa);
                }
            }
        }

        // Also look for "__Generated\0" (the generics pseudo-module)
        // Source: WriteGenericsPseudoCodeGenModule.cs
        byte[] genPattern = System.Text.Encoding.UTF8.GetBytes("__Generated\0");
        for (int i = rodataFileStart; i < rodataFileEnd - genPattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < genPattern.Length && match; j++)
                match = span[i + j] == genPattern[j];
            if (match)
            {
                ulong stringVa = rodata.Value.VirtualAddr + (ulong)(i - rodataFileStart);
                dllNameVAs.Add(stringVa);
            }
        }

        // Now find RELATIVE relocations that point to these string offsets.
        // r_addend = file offset of the string (since base = 0)
        // r_offset = VA where the pointer lives = start of Il2CppCodeGenModule
        foreach (var (rOffset, addend) in _elf.RelocationMap)
        {
            if (dllNameVAs.Contains(addend))
            {
                moduleVAs.Add(rOffset);
            }
        }

        return moduleVAs;
    }

    // ====================================================================
    // STEP 3: Find the g_CodeGenModules array
    //
    // Source: CodeRegistrationWriter.cs line 102:
    //   writer.WriteArrayInitializer("const Il2CppCodeGenModule*",
    //     "g_CodeGenModules", codeGenModules.Select(Emit.AddressOf), ...)
    //
    // The array is Il2CppCodeGenModule** — an array of pointers.
    // Each pointer is a RELATIVE relocation pointing to a module struct.
    // We find relocations whose addend ∈ moduleStructVAs, then check
    // if they form a contiguous 8-byte-spaced array.
    // ====================================================================
    private ulong FindCodeGenModulesArray(List<ulong> moduleStructVAs)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    RegistrationResolver.FindCodeGenModulesArray({moduleStructVAs.Count} module structs)");
        var moduleVASet = new HashSet<ulong>(moduleStructVAs);

        // Find all relocations that point to a module struct
        var arrayEntries = new List<ulong>();
        foreach (var (rOffset, addend) in _elf.RelocationMap)
        {
            if (moduleVASet.Contains(addend))
                arrayEntries.Add(rOffset);
        }

        if (arrayEntries.Count == 0) return 0;

        arrayEntries.Sort();

        // Find the longest contiguous run with pointer-sized spacing
        // (allowing one double-gap for misalignment)
        ulong expectedGap = (ulong)_ptrSize;
        ulong doubleGap = expectedGap * 2;
        ulong bestStart = arrayEntries[0];
        int bestCount = 1;
        ulong currentStart = arrayEntries[0];
        int currentCount = 1;

        for (int i = 1; i < arrayEntries.Count; i++)
        {
            ulong gap = arrayEntries[i] - arrayEntries[i - 1];
            if (gap == expectedGap || gap == doubleGap)
            {
                currentCount++;
                if (currentCount > bestCount)
                {
                    bestCount = currentCount;
                    bestStart = currentStart;
                }
            }
            else
            {
                currentStart = arrayEntries[i];
                currentCount = 1;
            }
        }

        return bestStart;
    }

    // ====================================================================
    // STEP 4: Find CodeRegistration
    //
    // Source: CodeRegistrationWriter.cs line 104-123:
    //   Il2CppCodeRegistration has codeGenModules at +128.
    //   There should be a RELATIVE relocation with:
    //     addend = g_CodeGenModules array VA
    //     r_offset = CodeRegistration VA + 128
    // ====================================================================
    private void FindCodeRegistration(ulong arrayVA)
    {
        foreach (var (rOffset, addend) in _elf.RelocationMap)
        {
            if (addend == arrayVA)
            {
                CodeRegistrationVA = rOffset - (ulong)CodeReg_codeGenModulesPtr;
                long fileOff = _elf.VirtualToFileOffset(CodeRegistrationVA);
                if (fileOff > 0)
                {
                    CodeGenModulesCount = (int)ReadUInt32At(fileOff + CodeReg_codeGenModulesCount);
                }
                break;
            }
        }
    }

    // ====================================================================
    // STEP 5: Parse all modules and extract method pointers
    //
    // Source: il2cpp-class-internals.h lines 585-604:
    //   Il2CppCodeGenModule {
    //     const char* moduleName;            // +0
    //     const uint32_t methodPointerCount;  // +8
    //     const Il2CppMethodPointer* methodPointers; // +16
    //     ...
    //   }
    //
    // Source: PerAssemblyCodeMetadataWriter.cs lines 102-133:
    //   methodPointers is "s_methodPointers" — an array of Il2CppMethodPointer
    //   indexed by method RID order within the assembly.
    //   NULL entries mean the method has no generated code (e.g., abstract/extern).
    // ====================================================================
    private bool ParseAllModules(ulong arrayVA, List<ulong> moduleStructVAs)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    RegistrationResolver.ParseAllModules(arrayVA=0x{arrayVA:X})");
        // Determine actual count from contiguous array
        // We walk from arrayVA reading pointers until we run out
        var allPointers = new List<ulong>();
        int moduleCount = CodeGenModulesCount > 0 ? CodeGenModulesCount : moduleStructVAs.Count;

        for (int i = 0; i < moduleCount; i++)
        {
            ulong entryVA = arrayVA + (ulong)(i * _ptrSize);
            ulong moduleVA = ReadRelocatedPointer(entryVA);
            if (moduleVA == 0) continue;

            long moduleFileOff = _elf.VirtualToFileOffset(moduleVA);
            if (moduleFileOff <= 0) continue;

            // Read moduleName via relocation
            ulong nameVA = ReadRelocatedPointer(moduleVA + (ulong)CGM_moduleName);
            long nameFileOff = _elf.VirtualToFileOffset(nameVA);
            string name = nameFileOff > 0 ? ReadStringAtFile(nameFileOff) : $"module_{i}";

            // Read methodPointerCount (uint32 at CGM_methodPointerCount, not relocated)
            uint methodCount = ReadUInt32At(moduleFileOff + CGM_methodPointerCount);

            // Read methodPointers ptr via relocation
            ulong methodPtrsVA = ReadRelocatedPointer(moduleVA + (ulong)CGM_methodPointers);

            if (methodCount == 0 || methodPtrsVA == 0)
            {
                // Add NULLs for methods with no code
                for (int j = 0; j < (int)methodCount; j++)
                    allPointers.Add(0);
                continue;
            }

            // Read each method pointer (each is also a relocation, pointer-sized)
            var pointers = new ulong[methodCount];
            for (int j = 0; j < (int)methodCount; j++)
            {
                ulong ptrEntryVA = methodPtrsVA + (ulong)(j * _ptrSize);
                pointers[j] = ReadRelocatedPointer(ptrEntryVA);
            }

            ModuleMethodPointers[name] = pointers;
            allPointers.AddRange(pointers);
        }

        AllMethodPointers = allPointers.ToArray();
        CodeGenModulesCount = moduleCount;
        return allPointers.Count > 0;
    }

    // ====================================================================
    // STEP 6: Find MetadataRegistration
    //
    // Source: il2cpp-class-internals.h lines 627-647
    //   MetadataRegistration is 128 bytes with 8 count/ptr pairs.
    //
    // Source: WriteCompilerCalculateFieldValues.cs:
    //   fieldOffsetsCount = typeInfos.Count (= TypeDefinitions count)
    // Source: WriteCompilerCalculateTypeValues.cs:
    //   typeDefinitionsSizesCount = typeInfos.Count (= TypeDefinitions count)
    //
    // Heuristic: Find a 128-byte struct in .data.rel.ro where:
    //   +80 (fieldOffsetsCount) == expectedTypeDefCount
    //   +96 (typeDefSizesCount) == expectedTypeDefCount
    //   +56 has a RELA entry (types pointer)
    //   +48 (typesCount) > 0 and reasonable
    //
    // Source: CodeRegistrationWriter.cs line 56-60:
    //   metadataUsagesCount is 0 for non-debugger/non-reload builds.
    // ====================================================================
    private void FindMetadataRegistration(int expectedTypeDefCount)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    RegistrationResolver.FindMetadataRegistration({expectedTypeDefCount})");
        // Scan all RELA-populated pointer fields to find the struct
        // We look for fieldOffsetsPtr having a relocation, and
        // the int32 at fieldOffsetsCount being == expectedTypeDefCount
        foreach (var (rOffset, _) in _elf.RelocationMap)
        {
            // If this rOffset could be fieldOffsetsPtr of a struct
            ulong candidateVA = rOffset - (ulong)MetaReg_fieldOffsetsPtr;
            long candidateFileOff = _elf.VirtualToFileOffset(candidateVA);
            if (candidateFileOff <= 0 || candidateFileOff + MetaReg_Size > _reader.Length)
                continue;

            // Check fieldOffsetsCount
            int fieldOffCount = (int)ReadUInt32At(candidateFileOff + MetaReg_fieldOffsetsCount);
            if (fieldOffCount != expectedTypeDefCount)
                continue;

            // Check typeDefSizesCount
            int tdsCount = (int)ReadUInt32At(candidateFileOff + MetaReg_typeDefSizesCount);
            if (tdsCount != expectedTypeDefCount)
                continue;

            // Check typesCount (must be > 0 and reasonable)
            int typesCount = (int)ReadUInt32At(candidateFileOff + MetaReg_typesCount);
            if (typesCount <= 0 || typesCount > 200000)
                continue;

            // Check types ptr has a relocation
            ulong typesPtrVA = candidateVA + (ulong)MetaReg_typesPtr;
            if (!_elf.RelocationMap.ContainsKey(typesPtrVA))
                continue;

            // FOUND IT
            MetadataRegistrationVA = candidateVA;
            TypesCount = typesCount;
            return;
        }
    }

    // ====================================================================
    // STEP 7: Parse Il2CppType table
    //
    // Source: WriteIl2CppTypeDefinitions.cs line 50:
    //   CodeTableName = context.Services.ContextScope.ForMetadataGlobalVar("g_Il2CppTypeTable")
    //
    // Source: il2cpp-runtime-metadata.h lines 54-76:
    //   Il2CppType = 12 bytes: { data(8), bitfield(4) }
    //
    // The types array is Il2CppType** — an array of pointers.
    // Each pointer is resolved via RELA and points to a 12-byte struct.
    // ====================================================================
    private void ParseTypeTable()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    RegistrationResolver.ParseTypeTable()");
        ulong typesPtrVA = MetadataRegistrationVA + (ulong)MetaReg_typesPtr;
        ulong typesArrayVA = ReadRelocatedPointer(typesPtrVA);
        if (typesArrayVA == 0) return;

        var types = new Il2CppType[TypesCount];
        int typeSizeOf = Il2CppType.GetSizeOf(_is32Bit);
        Span<byte> buf = stackalloc byte[typeSizeOf];

        for (int i = 0; i < TypesCount; i++)
        {
            ulong entryVA = typesArrayVA + (ulong)(i * _ptrSize);
            ulong typeVA = ReadRelocatedPointer(entryVA);
            if (typeVA == 0)
            {
                // NULL type entry — create a default
                types[i] = default;
                continue;
            }

            long typeFileOff = _elf.VirtualToFileOffset(typeVA);
            if (typeFileOff <= 0 || typeFileOff + typeSizeOf > _reader.Length)
            {
                types[i] = default;
                continue;
            }

            // Read bytes directly from the backing memory
            _reader.Span.Slice((int)typeFileOff, typeSizeOf).CopyTo(buf);
            var parsed = Il2CppType.Parse(buf, _is32Bit);

            // CRITICAL FIX: For pointer-based type tags, the data union (bytes 0-7)
            // is a pointer that is ZERO on disk in .data.rel.ro.
            // The linker populates it via R_AARCH64_RELATIVE relocations.
            //
            // Source Path: WriteIl2CppTypeDefinitions.cs lines 152-170:
            //   PTR:         data = "(void*)&" elementType
            //   SZARRAY:     data = "(void*)&" elementType  (rank == 1)
            //   GENERICINST: data = "&" genericClass
            //
            // Original Snippet (line 170):
            //   return "(void*)&" + context.Global.Services.Naming.ForIl2CppType(context, arrayType.ElementType);
            //
            // Mapping Logic: The `data` field at typeVA+0 is a relocation target.
            //   We must resolve it through the relocation map.
            if (parsed.TypeEnum is
                Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY or
                Il2CppTypeEnum.IL2CPP_TYPE_PTR or
                Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST or
                Il2CppTypeEnum.IL2CPP_TYPE_ARRAY)
            {
                ulong resolvedData = ReadRelocatedPointer(typeVA);
                parsed = parsed with { Data = resolvedData };
            }

            types[i] = parsed;
        }

        Types = types;
    }

    // ====================================================================
    // STEP 6a: Parse Field Offsets Table
    //
    // Source: il2cpp-class-internals.h:640-641:
    //   FieldIndex fieldOffsetsCount;
    //   const int32_t** fieldOffsets;
    //
    // Source: GlobalMetadata.cpp:1097-1103:
    //   int32_t offset = s_Il2CppMetadataRegistration->fieldOffsets[typeDefIndex][fieldIndexInType];
    //
    // Structure: int32_t** = pointer to array of pointers.
    //   fieldOffsets[typeDefIndex] → pointer to int32_t array (or NULL for types with no fields)
    //   fieldOffsets[typeDefIndex][fieldIndexInType] → byte offset of that field
    //
    // The outer pointer array entries are resolved via R_AARCH64_RELATIVE relocations.
    // The inner int32 arrays are stored inline in .data.rel.ro as contiguous int32 values.
    // ====================================================================
    private void ParseFieldOffsetsTable(int typeDefCount, int[]? fieldCounts)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    RegistrationResolver.ParseFieldOffsetsTable({typeDefCount})");
        ulong fieldOffsetsPtrVA = MetadataRegistrationVA + (ulong)MetaReg_fieldOffsetsPtr;
        ulong fieldOffsetsArrayVA = ReadRelocatedPointer(fieldOffsetsPtrVA);
        if (fieldOffsetsArrayVA == 0)
        {
            FieldOffsets = new int[]?[typeDefCount];
            return;
        }

        var result = new int[]?[typeDefCount];

        for (int typeIdx = 0; typeIdx < typeDefCount; typeIdx++)
        {
            // Each entry in the outer array is a pointer (ptrSize bytes)
            ulong entryVA = fieldOffsetsArrayVA + (ulong)(typeIdx * _ptrSize);
            ulong innerArrayVA = ReadRelocatedPointer(entryVA);

            if (innerArrayVA == 0)
            {
                // No field offsets for this type (types with 0 fields)
                result[typeIdx] = null;
                continue;
            }

            long innerFileOff = _elf.VirtualToFileOffset(innerArrayVA);
            if (innerFileOff <= 0)
            {
                result[typeIdx] = null;
                continue;
            }

            // Actually, we'll read all field offsets eagerly with a max of 256 fields per type.
            // This is safe because types with >256 fields are extremely rare in Unity IL2CPP.
            result[typeIdx] = ReadFieldOffsetsAtVA(innerArrayVA, fieldCounts != null && typeIdx < fieldCounts.Length ? fieldCounts[typeIdx] : -1);
        }

        FieldOffsets = result;
    }

    // ====================================================================
    // STEP 6b: Parse TypeDef Sizes Table
    // ====================================================================
    private void ParseTypeDefSizesTable(int typeDefCount)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    RegistrationResolver.ParseTypeDefSizesTable({typeDefCount})");
        ulong sizesPtrVA = MetadataRegistrationVA + (ulong)MetaReg_typeDefSizesPtr;
        ulong sizesArrayVA = ReadRelocatedPointer(sizesPtrVA);
        if (sizesArrayVA == 0)
        {
            TypeDefSizes = new Il2CppTypeDefinitionSizes[typeDefCount];
            return;
        }

        var result = new Il2CppTypeDefinitionSizes[typeDefCount];
        long sizesFileOff = _elf.VirtualToFileOffset(sizesArrayVA);
        if (sizesFileOff > 0 && sizesFileOff + (typeDefCount * 16) <= _reader.Length)
        {
            _reader.Position = (int)sizesFileOff;
            for (int i = 0; i < typeDefCount; i++)
            {
                result[i] = new Il2CppTypeDefinitionSizes
                {
                    instance_size = _reader.ReadUInt32(),
                    native_size = _reader.ReadInt32(),
                    static_fields_size = _reader.ReadUInt32(),
                    thread_static_fields_size = _reader.ReadUInt32()
                };
            }
        }
        TypeDefSizes = result;
    }

    /// <summary>
    /// Read field offset int32 values from a VA.
    /// If fieldCount is known (>= 0), reads exactly that many values.
    /// Otherwise falls back to a generous max of 1024 (up from the old 256).
    /// </summary>
    private int[]? ReadFieldOffsetsAtVA(ulong va, int fieldCount)
    {
        long fileOff = _elf.VirtualToFileOffset(va);
        if (fileOff <= 0) return null;

        // Use exact field count from metadata when available, otherwise fallback
        int maxFields = fieldCount >= 0 ? fieldCount : 1024;
        if (maxFields == 0) return Array.Empty<int>();

        var offsets = new List<int>(Math.Min(maxFields, 64));

        for (int i = 0; i < maxFields; i++)
        {
            long pos = fileOff + i * 4;
            if (pos + 4 > _reader.Length) break;

            _reader.Position = (int)pos;
            int val = _reader.ReadInt32();

            offsets.Add(val);
        }

        return offsets.Count > 0 ? offsets.ToArray() : null;
    }

    // ====================================================================
    // STEP 6b: Parse MethodSpec Table (g_Il2CppMethodSpecTable)
    //
    // Source Path: WriteIl2CppGenericMethodDefinitions.cs lines 47-53
    //
    // Each Il2CppMethodSpec entry:
    //   methodDefinitionIndex: int32  (+0)
    //   classInstIndex:        int32  (+4)
    //   methodInstIndex:       int32  (+8)
    //   Total = 12 bytes
    //
    // Located at MetadataRegistration+72, count at MetadataRegistration+64.
    // This is a flat array (NOT pointer-based like the types table).
    // ====================================================================

    private void ParseMethodSpecTable()
    {
        ulong countVA = MetadataRegistrationVA + (ulong)MetaReg_methodSpecsCount;
        long countFileOff = _elf.VirtualToFileOffset(countVA);
        if (countFileOff <= 0) return;
        int count = BitConverter.ToInt32(_reader.Span.Slice((int)countFileOff, 4));
        if (count <= 0) return;

        ulong ptrVA = MetadataRegistrationVA + (ulong)MetaReg_methodSpecsPtr;
        ulong tableVA = ReadRelocatedPointer(ptrVA);
        if (tableVA == 0) return;

        long tableFileOff = _elf.VirtualToFileOffset(tableVA);
        if (tableFileOff <= 0) return;

        var specs = new MethodSpecDef[count];
        ReadOnlySpan<byte> span = _reader.Span;
        for (int i = 0; i < count; i++)
        {
            int off = (int)tableFileOff + i * 12;
            if (off + 12 > span.Length) break;

            specs[i] = new MethodSpecDef
            {
                MethodDefinitionIndex = BitConverter.ToInt32(span.Slice(off, 4)),
                ClassInstIndex        = BitConverter.ToInt32(span.Slice(off + 4, 4)),
                MethodInstIndex       = BitConverter.ToInt32(span.Slice(off + 8, 4)),
            };
        }

        MethodSpecs = specs;
    }

    // ====================================================================
    // STEP 6d: Parse Il2CppGenericInst Pointer Table
    //
    // Source: il2cpp-class-internals.h lines 628-630:
    //   int32_t genericInstsCount;            // MetadataRegistration+16
    //   Il2CppGenericInst** genericInsts;      // MetadataRegistration+24
    //
    // This is a pointer-of-pointers table. Each entry (after relocation)
    // is the VA of an Il2CppGenericInst struct:
    //   typedef struct Il2CppGenericInst {
    //     uint32_t type_argc;         // +0
    //     const Il2CppType **type_argv; // +8
    //   } Il2CppGenericInst;
    //
    // Indexed by MethodSpec.ClassInstIndex and MethodSpec.MethodInstIndex
    // to resolve generic type arguments like <AudioSource> or <string, int>.
    // ====================================================================

    private void ParseGenericInstTable()
    {
        ulong countVA = MetadataRegistrationVA + (ulong)MetaReg_genericInstsCount;
        long countFileOff = _elf.VirtualToFileOffset(countVA);
        if (countFileOff <= 0) return;
        int count = BitConverter.ToInt32(_reader.Span.Slice((int)countFileOff, 4));
        if (count <= 0) return;

        ulong ptrVA = MetadataRegistrationVA + (ulong)MetaReg_genericInstsPtr;
        ulong tableVA = ReadRelocatedPointer(ptrVA);
        if (tableVA == 0) return;

        // Each entry in the table is a pointer (8 bytes) to an Il2CppGenericInst.
        // Resolve each pointer through the relocation map.
        var vas = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            ulong entryVA = tableVA + (ulong)(i * _ptrSize);
            vas[i] = ReadRelocatedPointer(entryVA);
        }

        GenericInstVAs = vas;
    }

    // ====================================================================
    // STEP 6c: Parse Generic Method Table (g_Il2CppGenericMethodFunctions)
    //
    // Source Path: WriteIl2CppGenericMethodTable.cs line 20:
    //   "{ " + m.TableIndex + ", " + m.PointerTableIndex + ", "
    //        + invokerIndex + ", " + m.AdjustorThunkTableIndex + "}"
    //
    // Each Il2CppGenericMethodFunctionsDefinitions entry:
    //   genericMethodIndex:      int32  (+0)  — index into MethodSpecs
    //   pointerTableIndex:       int32  (+4)  — index into genericMethodPointers
    //   invokerIndex:            int32  (+8)
    //   adjustorThunkTableIndex: int32  (+12)
    //   Total = 16 bytes
    //
    // Located at MetadataRegistration+40, count at MetadataRegistration+32.
    // ====================================================================

    private void ParseGenericMethodTable()
    {
        ulong countVA = MetadataRegistrationVA + (ulong)MetaReg_genericMethodTableCount;
        long countFileOff = _elf.VirtualToFileOffset(countVA);
        if (countFileOff <= 0) return;
        int count = BitConverter.ToInt32(_reader.Span.Slice((int)countFileOff, 4));
        if (count <= 0) return;

        ulong ptrVA = MetadataRegistrationVA + (ulong)MetaReg_genericMethodTablePtr;
        ulong tableVA = ReadRelocatedPointer(ptrVA);
        if (tableVA == 0) return;

        long tableFileOff = _elf.VirtualToFileOffset(tableVA);
        if (tableFileOff <= 0) return;

        var entries = new GenericMethodFuncDef[count];
        ReadOnlySpan<byte> span = _reader.Span;
        for (int i = 0; i < count; i++)
        {
            int off = (int)tableFileOff + i * 16;
            if (off + 16 > span.Length) break;

            entries[i] = new GenericMethodFuncDef
            {
                GenericMethodIndex     = BitConverter.ToInt32(span.Slice(off, 4)),
                PointerTableIndex      = BitConverter.ToInt32(span.Slice(off + 4, 4)),
                InvokerIndex           = BitConverter.ToInt32(span.Slice(off + 8, 4)),
                AdjustorThunkTableIndex = BitConverter.ToInt32(span.Slice(off + 12, 4)),
            };
        }

        GenericMethodTable = entries;
    }

    // ====================================================================
    // STEP 7: Parse Generic Method Pointers from CodeRegistration
    //
    // Source: CodeRegistrationWriter.cs lines 108-109:
    //   genericMethodPointerTable.Count  → at CodeRegistration+16
    //   genericMethodPointerTable.Name   → at CodeRegistration+24
    //
    // This is an array of function pointers, indexed by
    // GenericMethodFuncDef.PointerTableIndex.
    // Each entry is a relocated pointer (8 bytes on ARM64).
    // ====================================================================

    private void ParseGenericMethodPointers()
    {
        ulong countVA = CodeRegistrationVA + (ulong)CodeReg_genericMethodPointersCount;
        long countFileOff = _elf.VirtualToFileOffset(countVA);
        if (countFileOff <= 0) return;
        int count = BitConverter.ToInt32(_reader.Span.Slice((int)countFileOff, 4));
        if (count <= 0) return;

        ulong ptrVA = CodeRegistrationVA + (ulong)CodeReg_genericMethodPointersPtr;
        ulong tableVA = ReadRelocatedPointer(ptrVA);
        if (tableVA == 0) return;

        var pointers = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            ulong entryVA = tableVA + (ulong)(i * _ptrSize);
            pointers[i] = ReadRelocatedPointer(entryVA);
        }

        GenericMethodPointers = pointers;
    }

    // ====================================================================
    // STEP: Parse MetadataUsages table (V24 only)
    //
    // Source: il2cpp-class-internals.h:
    //   const size_t metadataUsagesCount;    // +112
    //   void*** metadataUsages;               // +120
    //
    // The metadataUsages array is void** — an array of pointers to .bss
    // variables. Each pointer is resolved via RELA and points to a .bss
    // address where the runtime stores the resolved metadata object.
    //
    // CRITICAL: In V24, the binary's metadataUsagesCount only covers
    // non-string metadata usages. String literal usages extend BEYOND
    // this count in the same contiguous array. The true upper bound
    // comes from the max destinationIndex in metadataUsagePairs.
    // ====================================================================
    public void ParseMetadataUsagesTable(int maxUsageIndex = -1)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    RegistrationResolver.ParseMetadataUsagesTable()");

        ulong countVA = MetadataRegistrationVA + (ulong)MetaReg_metadataUsagesCount;
        long countFileOff = _elf.VirtualToFileOffset(countVA);
        if (countFileOff <= 0) return;

        // Read as size_t (pointer-sized: 4 bytes on 32-bit, 8 on 64-bit)
        long count;
        if (_is32Bit)
        {
            count = BitConverter.ToInt32(_reader.Span.Slice((int)countFileOff, 4));
        }
        else
        {
            count = BitConverter.ToInt64(_reader.Span.Slice((int)countFileOff, 8));
        }
        if (count <= 0 || count > 100_000)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      metadataUsagesCount={count} — skipping (out of range)");
            return;
        }

        // Extend count if pairs table has higher destination indices
        // (V24 string literal usages are contiguous past the stated count)
        long effectiveCount = count;
        if (maxUsageIndex >= 0 && maxUsageIndex + 1 > effectiveCount)
        {
            ConsoleReporter.Info($"  MetadataUsages: extending from {effectiveCount} to {maxUsageIndex + 1} (string literal extension)");
            effectiveCount = maxUsageIndex + 1;
        }

        ulong ptrVA = MetadataRegistrationVA + (ulong)MetaReg_metadataUsagesPtr;
        ulong tableVA = ReadRelocatedPointer(ptrVA);
        if (tableVA == 0)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      metadataUsages pointer is null — skipping");
            return;
        }

        var addresses = new ulong[(int)effectiveCount];
        for (int i = 0; i < (int)effectiveCount; i++)
        {
            ulong entryVA = tableVA + (ulong)(i * _ptrSize);
            addresses[i] = ReadRelocatedPointer(entryVA);
        }

        MetadataUsageAddresses = addresses;
        ConsoleReporter.Info($"  MetadataUsages: {effectiveCount} entries parsed from binary ({count} stated + {effectiveCount - count} extended)");
    }

    public void ClearRelocations()
    {
        _elf.RelocationMap.Clear();
    }
}
