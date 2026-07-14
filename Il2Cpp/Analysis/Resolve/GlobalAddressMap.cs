using Rosetta.Binary;
using Rosetta.Common;
using Rosetta.Metadata;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.Resolve;

/// <summary>
/// Builds a complete map of VA → metadata meaning by combining:
///   1. Relocation map (what pointers are stored where)
///   2. Method address map (code VAs → method names)
///   3. ELF section boundaries (.bss = init flags, .data.rel.ro = metadata ptrs)
///   4. Encoded metadata tokens from relocated pointer values
///   5. GOT indirection following (for PIC code)
///   6. Section-based classification fallback
///
/// Source: MetadataUsageWriter.cs — emits global vars like:
///   RuntimeClass* TypeName_il2cpp_TypeInfo_var = (RuntimeClass*)(uintptr_t)encodedToken;
///
/// Source: MetadataUtils.cs line 265-268:
///   encodedToken = (type &lt;&lt; 29) | (index &lt;&lt; 1) | 1
///   type: 0=Invalid, 1=Il2CppClass, 2=Il2CppType, 3=MethodInfo,
///         4=FieldInfo, 5=StringLiteral, 6=MethodRef, 7=FieldRva
///
/// Source: il2cpp-codegen.cpp line 182-185:
///   il2cpp_codegen_initialize_runtime_metadata_inline resolves the token
///   at runtime and stores the resolved pointer back at the same address.
/// </summary>
public sealed class GlobalAddressMap
{
    private readonly Dictionary<ulong, AddressAnnotation> _map = new();

    public int Count => _map.Count;

    public AddressAnnotation? Lookup(ulong va) =>
        _map.TryGetValue(va, out var ann) ? ann : null;

    public IReadOnlyDictionary<ulong, AddressAnnotation> All => _map;

    public Dictionary<uint, string> Il2CppDefaultsLayout { get; } = new();

    // Phase 1: Map code addresses to method names
    public void RegisterMethodPointers(Dictionary<ulong, string> methodAddressToName)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"GlobalAddressMap.RegisterMethodPointers: {methodAddressToName.Count} entries");
        int added = 0;
        foreach (var (va, name) in methodAddressToName)
        {
            if (va == 0) continue;
            _map[va] = new AddressAnnotation
            {
                Address = va,
                Kind = AddressKind.MethodPointer,
                Label = name,
            };
            added++;
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  registered {added} method pointers");
    }

    // Phase 2: .bss region for init flag detection
    public void RegisterBssRegion(ulong bssStart, ulong bssSize)
    {
        _bssStart = bssStart;
        _bssEnd = bssStart + bssSize;
    }

    private ulong _bssStart, _bssEnd;
    private int _metadataVersion;

    // Phase 3: Build name lookup indexes from metadata
    public void RegisterMetadataUsages(RegistrationResolver resolver, MetadataParser metadata, TypeResolver typeResolver)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"GlobalAddressMap.RegisterMetadataUsages");
        _typeResolver = typeResolver;
        _metadataVersion = metadata.EffectiveVersion;
        if (_metadataVersion < 27)
        {
            Rosetta.Common.Constants.ClassInitFlagOffset = 0xE0;
        }
        else
        {
            Rosetta.Common.Constants.ClassInitFlagOffset = 0xE4;
        }
        BuildNameIndexes(metadata, resolver);
        ScanRelocationsForTokens(resolver);
        BuildBssAnnotationMap(resolver, metadata);
        
        ExtractIl2CppDefaultsLayout(resolver);
    }

    /// <summary>
    /// Phase 4: Register GOT indirection and section classification resolvers.
    ///
    /// Source: ELF AArch64 ABI — GOT entries use R_AARCH64_RELATIVE
    ///   relocations in .rela.dyn to store the ultimate target VA.
    /// Source: MetadataUsageWriter.cs — metadata variables are in .bss,
    ///   accessed via GOT in PIC code.
    /// </summary>
    public void RegisterSectionResolvers(GotIndirectionResolver gotResolver, ElfSectionClassifier sectionClassifier)
    {
        _gotResolver = gotResolver;
        _sectionClassifier = sectionClassifier;
    }

    private GotIndirectionResolver? _gotResolver;
    private ElfSectionClassifier? _sectionClassifier;

    private void BuildNameIndexes(MetadataParser metadata, RegistrationResolver resolver)
    {
        for (int i = 0; i < metadata.MethodDefinitions.Length; i++)
        {
            var md = metadata.MethodDefinitions[i];
            _methodNameByIndex[i] = md.Name ?? $"method_{i}";
        }

        for (int i = 0; i < metadata.StringLiterals.Length; i++)
        {
            string lit = metadata.StringLiterals[i];
            _stringByIndex[i] = lit.Length > 40 ? lit[..40] + "..." : lit;
        }

        // Build field name index from FieldRefs → FieldDefinitions
        //
        // CRITICAL: FieldRef.FieldIndex is a WITHIN-TYPE field index, not a
        // global FieldDefinition index. To get the actual FieldDef:
        //   1. Resolve FieldRef.TypeIndex → Il2CppType → TypeDefinition
        //   2. Get TypeDef.FieldStart
        //   3. Actual FieldDef index = FieldStart + FieldRef.FieldIndex
        //
        // Source: Il2CppFieldRef.cs — FieldIndex is the field's position
        // within the declaring type's field list.
        for (int i = 0; i < metadata.FieldRefs.Length; i++)
        {
            var fr = metadata.FieldRefs[i];

            // Resolve the declaring type to find FieldStart
            int globalFieldDefIndex = -1;
            string? typeName = null;
            if (_typeResolver != null)
            {
                var il2cppType = _typeResolver.GetTypeByIndex(fr.TypeIndex);
                if (il2cppType.HasValue &&
                    (il2cppType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_CLASS ||
                     il2cppType.Value.TypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE))
                {
                    int typeDefIdx = il2cppType.Value.KlassIndex;
                    if (typeDefIdx >= 0 && typeDefIdx < metadata.TypeDefinitions.Length)
                    {
                        var td = metadata.TypeDefinitions[typeDefIdx];
                        typeName = td.FullName;
                        int fieldStart = td.FieldStart;
                        if (fieldStart >= 0)
                            globalFieldDefIndex = fieldStart + fr.FieldIndex;
                    }
                }
                typeName ??= _typeResolver.ResolveTypeName(fr.TypeIndex);
            }

            // Look up the field name from the correctly computed global index
            if (globalFieldDefIndex >= 0 && globalFieldDefIndex < metadata.FieldDefinitions.Length)
            {
                var fd = metadata.FieldDefinitions[globalFieldDefIndex];
                if (fd.Name != null)
                {
                    _fieldNameByIndex[i] = typeName != null
                        ? $"{typeName}.{fd.Name}"
                        : fd.Name;
                }
            }
        }

        // Build generic method name index from MethodSpecs → MethodDefinitions
        // MethodRef tokens (type 6) use the MethodSpec index directly.
        // MethodSpec[i].MethodDefinitionIndex → MethodDefinitions[defIdx].Name
        // MethodSpec[i].MethodInstIndex → GenericInst → <T> (method-level generics)
        // MethodSpec[i].ClassInstIndex → GenericInst → <T> (class-level generics)
        var specs = resolver.MethodSpecs;
        for (int i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            if (spec.MethodDefinitionIndex >= 0 && spec.MethodDefinitionIndex < metadata.MethodDefinitions.Length)
            {
                var md = metadata.MethodDefinitions[spec.MethodDefinitionIndex];
                string methodName = md.Name ?? $"Method_{spec.MethodDefinitionIndex}";

                // Build qualified name with declaring type
                string? typeName = null;
                if (md.DeclaringTypeIndex >= 0 && md.DeclaringTypeIndex < metadata.TypeDefinitions.Length)
                {
                    typeName = metadata.TypeDefinitions[md.DeclaringTypeIndex].FullName;
                }

                // Resolve method-level generic args (e.g., GetComponent<AudioSource>)
                if (spec.MethodInstIndex >= 0 && _typeResolver != null)
                {
                    string? methodArgs = _typeResolver.ResolveGenericInstArgs(spec.MethodInstIndex);
                    if (methodArgs != null)
                        methodName += methodArgs;
                }

                // Resolve class-level generic args (e.g., List<int>::Add)
                if (spec.ClassInstIndex >= 0 && _typeResolver != null && typeName != null)
                {
                    string? classArgs = _typeResolver.ResolveGenericInstArgs(spec.ClassInstIndex);
                    if (classArgs != null)
                    {
                        // Strip backtick-arity suffix: "List`1" → "List"
                        // Source: NamingExtensions.cs lines 60-68
                        typeName = Rosetta.Analysis.Utils.TypeUtils.StripAritySuffix(typeName);
                        typeName += classArgs;
                    }
                }

                _genericMethodNameByIndex[i] = typeName != null
                    ? $"{typeName}::{methodName}"
                    : methodName;
            }
        }
    }

    /// <summary>
    /// Scan all RELATIVE relocations for encoded metadata tokens.
    ///
    /// In IL2CPP codegen, metadata usage variables are initialized like:
    ///   RuntimeClass* varName = (RuntimeClass*)(uintptr_t)encodedToken;
    ///
    /// On ARM64 Android, these become R_AARCH64_RELATIVE relocations where:
    ///   r_offset = VA of the global variable (in .data.rel.ro)
    ///   r_addend = the encoded token value (cast to a pointer)
    ///
    /// The token encodes: (type &lt;&lt; 29) | (index &lt;&lt; 1) | 1
    /// Valid tokens have bit 0 set and bits 29-31 in range [1..6].
    /// </summary>
    private void ScanRelocationsForTokens(RegistrationResolver resolver)
    {
        // Read each relocated pointer value and try to decode as a token
        // We use the ReadRelocatedPointer which returns the r_addend
        // Small addend values (< 0x1000000) that match the token pattern
        // are metadata usage tokens

        // Instead of walking ALL relocations (expensive), we resolve on demand.
        // Store the resolver reference for lazy lookup.
        _resolver = resolver;
    }

    /// <summary>
    /// Build .bss address → annotation map for V24 metadata resolution.
    ///
    /// V24 stores metadata token mappings in two tables:
    ///   1. MetadataUsageAddresses[usageIndex] → .bss VA (from binary's MetadataRegistration)
    ///   2. MetadataUsagePairsRaw[i] → (destinationIndex, encodedToken) (from global-metadata.dat)
    ///
    /// By combining these, we can map .bss addresses to their resolved types/strings
    /// at static analysis time, eliminating "metadata_var" labels.
    /// </summary>
    private void BuildBssAnnotationMap(RegistrationResolver resolver, MetadataParser metadata)
    {
        var usageAddresses = resolver.MetadataUsageAddresses;
        var pairsRaw = metadata.MetadataUsagePairsRaw;

        if (usageAddresses.Length == 0 || pairsRaw.Length == 0)
            return;

        ConsoleReporter.Info($"  BuildBssAnnotationMap: {usageAddresses.Length} usage addresses, {pairsRaw.Length / 8} pairs");

        // Build destinationIndex → encodedToken map from the pairs table
        int numPairs = pairsRaw.Length / 8;
        var destToToken = new Dictionary<int, uint>(numPairs);
        for (int i = 0; i < numPairs; i++)
        {
            int destIdx = BitConverter.ToInt32(pairsRaw, i * 8);
            uint encoded = BitConverter.ToUInt32(pairsRaw, i * 8 + 4);
            // Use last occurrence for duplicates (same dest can appear in multiple lists)
            destToToken[destIdx] = encoded;
        }

        // For each usage address, look up the token and decode it
        int resolved = 0;
        for (int usageIdx = 0; usageIdx < usageAddresses.Length; usageIdx++)
        {
            ulong bssAddr = usageAddresses[usageIdx];
            if (bssAddr == 0) continue;

            if (!destToToken.TryGetValue(usageIdx, out uint encoded))
                continue;

            var annotation = DecodeToken(encoded, bssAddr);
            if (annotation != null && annotation.Kind != AddressKind.Unknown)
            {
                _bssToAnnotation[bssAddr] = annotation;
                resolved++;
            }
        }

        ConsoleReporter.Info($"  BuildBssAnnotationMap: resolved {resolved}/{usageAddresses.Length} .bss entries");
    }

    private RegistrationResolver? _resolver;
    private TypeResolver? _typeResolver;
    private readonly Dictionary<int, string> _methodNameByIndex = new();
    private readonly Dictionary<int, string> _stringByIndex = new();
    private readonly Dictionary<int, string> _fieldNameByIndex = new();
    private readonly Dictionary<int, string> _genericMethodNameByIndex = new();

    /// <summary>
    /// V24 .bss address → pre-resolved annotation map.
    /// Built from metadataUsagePairs + metadataUsageAddresses tables.
    /// </summary>
    private readonly Dictionary<ulong, AddressAnnotation> _bssToAnnotation = new();

    public bool IsInBss(ulong va) => va >= _bssStart && va < _bssEnd;

    /// <summary>
    /// Decode an encoded metadata usage token.
    /// V27+ encoding: (type &lt;&lt; 29) | (index &lt;&lt; 1) | 1
    /// V24 encoding:  (type &lt;&lt; 29) | index  (no bit0 marker)
    /// Source: MetadataUtils.cs line 265-268
    /// </summary>
    public AddressAnnotation? DecodeToken(uint encoded, ulong address = 0)
    {
        int type;
        int index;

        // V24 always uses (type << 29) | index — bit0 is part of the index, not a marker.
        // V27+ uses (type << 29) | (index << 1) | 1 — bit0=1 is a marker.
        if (_metadataVersion < 27)
        {
            // V24 encoding: top 3 bits = type, lower 29 bits = raw index
            type = (int)(encoded >> 29) & 0x7;
            index = (int)(encoded & 0x1FFFFFFF);
        }
        else if ((encoded & 1) == 1)
        {
            // V27+ encoding: (type << 29) | (index << 1) | 1
            type = (int)(encoded >> 29) & 0x7;
            index = (int)((encoded >> 1) & 0x0FFFFFFF);
        }
        else
        {
            // V27+ with bit0=0 — NOT a valid metadata token
            return null;
        }

        if (type == 0 || type > 7) return null;

        var kind = type switch
        {
            1 => AddressKind.RuntimeClass,
            2 => AddressKind.RuntimeType,
            3 => AddressKind.MethodInfo,
            4 => AddressKind.FieldInfo,
            5 => AddressKind.StringLiteral,
            6 => AddressKind.MethodRef,
            7 => AddressKind.FieldRva,
            _ => AddressKind.Unknown,
        };

        string label = kind switch
        {
            AddressKind.RuntimeClass  => _typeResolver?.ResolveTypeName(index) ?? $"type_{index}",
            AddressKind.RuntimeType   => _typeResolver?.ResolveTypeName(index) ?? $"type_{index}",
            AddressKind.MethodInfo    => _methodNameByIndex.GetValueOrDefault(index, $"method_{index}"),
            AddressKind.StringLiteral => _stringByIndex.GetValueOrDefault(index, $"string_{index}"),
            AddressKind.MethodRef     => _genericMethodNameByIndex.GetValueOrDefault(index, $"generic_method_{index}"),
            AddressKind.FieldInfo     => _fieldNameByIndex.GetValueOrDefault(index, $"field_{index}"),
            AddressKind.FieldRva      => _fieldNameByIndex.GetValueOrDefault(index, $"fieldRva_{index}"),
            _ => $"unknown_{index}",
        };

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"    DecodeToken(0x{encoded:X8}): type={type} index={index} kind={kind} label=\"{label}\"");
        return new AddressAnnotation
        {
            Address = address,
            Kind = kind,
            MetadataIndex = index,
            Label = label,
        };
    }

    /// <summary>
    /// Resolve an ADRP+LDR target address to an annotation.
    /// Steps:
    ///   1. Check if cached
    ///   2. LDRB from .bss → class init flag
    ///   3. LDR from .data.rel.ro → read relocated value → try decode as token
    ///   4. Follow GOT indirection → resolve the ultimate target recursively
    ///   5. Read raw data at VA → try decode as metadata token
    ///   6. Section-based classification fallback
    /// </summary>
    public AddressAnnotation? ResolveAddress(ulong targetVA, bool isByteLdrb = false)
    {
        if (_map.TryGetValue(targetVA, out var cached))
            return cached;

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    ResolveAddress(0x{targetVA:X}) isLdrb={isByteLdrb}");


        // LDRB/STRB from .bss → class init flag
        if (isByteLdrb && IsInBss(targetVA))
        {
            var ann = new AddressAnnotation
            {
                Address = targetVA,
                Kind = AddressKind.ClassInitFlag,
                Label = "class_init_flag",
            };
            _map[targetVA] = ann;
            return ann;
        }

        // Step 3: Try to decode the relocated pointer value at this address.
        //
        // CRITICAL FIX: The r_addend from .data.rel.ro relocations is always a large VA
        // pointing to a runtime struct in .data (Il2CppFieldInfo, Il2CppClass*, etc.).
        // The previous code followed this VA and read uint32 from the struct, which
        // produced false positives — the struct's initialization data bytes (e.g., 0x8000000D)
        // can match the token format (bit0=1, type=4, index=6) but are NOT actual tokens.
        //
        // Correct behavior: only try to decode the r_addend itself as a token if it's
        // a small value. In practice, all r_addend values are large VAs (>16MB), so
        // this path is a no-op. The actual token resolution happens correctly via
        // GOT indirection (Step 4) for .bss metadata variables.
        if (_resolver != null)
        {
            ulong dataVA = _resolver.ReadRelocatedPointer(targetVA);
            if (dataVA > 0)
            {
                // A metadata token is an encoded integer. A VA is a pointer.
                // If dataVA points to a valid file offset in the ELF, it is a VA to a runtime struct.
                // Otherwise, it is an encoded token.
                long fileOffset = _resolver.ElfParser.VirtualToFileOffset(dataVA);
                if (fileOffset < 0 && dataVA <= uint.MaxValue)
                {
                    var decoded = DecodeToken((uint)dataVA, targetVA);
                    if (decoded != null && decoded.Kind != AddressKind.Unknown)
                    {
                        _map[targetVA] = decoded;
                        return decoded;
                    }
                }
            }
        }

        // Step 4: Follow GOT indirection
        // Source: ELF AArch64 ABI — PIC code accesses globals via GOT.
        // ADRP+LDR loads a GOT entry, which has an R_AARCH64_RELATIVE
        // relocation pointing to the ultimate target in .bss/.data/.data.rel.ro.
        if (_gotResolver != null)
        {
            ulong? ultimateTarget = _gotResolver.FollowIndirection(targetVA);
            if (ultimateTarget.HasValue)
            {
                ulong target = ultimateTarget.Value;

                // Recursively resolve the ultimate target (but not through GOT again)
                // First check if it's a .bss metadata variable
                if (IsInBss(target))
                {
                    // Check V24 pre-resolved map first
                    if (_bssToAnnotation.TryGetValue(target, out var bssAnn))
                    {
                        // Return a copy with the GOT VA as the address
                        var resolved = new AddressAnnotation
                        {
                            Address = targetVA,
                            Kind = bssAnn.Kind,
                            MetadataIndex = bssAnn.MetadataIndex,
                            Label = bssAnn.Label,
                        };
                        _map[targetVA] = resolved;
                        return resolved;
                    }

                    var ann = new AddressAnnotation
                    {
                        Address = targetVA,
                        Kind = AddressKind.BssVariable,
                        Label = "metadata_var",
                    };
                    _map[targetVA] = ann;
                    return ann;
                }

                // Try reading a metadata token at the ultimate target
                if (_resolver != null)
                {
                    uint directToken = _resolver.ReadUInt32(target);
                    if (directToken > 0)
                    {
                        var decoded = DecodeToken(directToken, targetVA);
                        if (decoded != null && decoded.Kind != AddressKind.Unknown)
                        {
                            _map[targetVA] = decoded;
                            return decoded;
                        }
                    }

                    // Try reading a relocated pointer at the ultimate target.
                    // Only decode as token if the value is not a valid VA.
                    // VAs to runtime structs produce false-positive token matches if followed.
                    ulong relocatedVal = _resolver.ReadRelocatedPointer(target);
                    if (relocatedVal > 0 && relocatedVal != target)
                    {
                        long fileOffset = _resolver.ElfParser.VirtualToFileOffset(relocatedVal);
                        if (fileOffset < 0 && relocatedVal <= uint.MaxValue)
                        {
                            var decoded = DecodeToken((uint)relocatedVal, targetVA);
                            if (decoded != null && decoded.Kind != AddressKind.Unknown)
                            {
                                _map[targetVA] = decoded;
                                return decoded;
                            }
                        }
                    }
                }

                // The ultimate target exists but we can't decode a token —
                // classify by the section of the ultimate target
                if (_sectionClassifier != null)
                {
                    var sectionAnn = _sectionClassifier.Classify(target);
                    if (sectionAnn != null)
                    {
                        // Override the address to be the original target VA
                        var ann = new AddressAnnotation
                        {
                            Address = targetVA,
                            Kind = sectionAnn.Kind,
                            Label = sectionAnn.Label,
                        };
                        _map[targetVA] = ann;
                        return ann;
                    }
                }
            }
        }

        // Step 5: Try reading raw data at the target VA as a metadata token
        // Source: MetadataUsageWriter.cs — some .data variables store tokens directly
        if (_resolver != null)
        {
            uint rawToken = _resolver.ReadUInt32(targetVA);
            if (rawToken > 0)
            {
                var decoded = DecodeToken(rawToken, targetVA);
                if (decoded != null && decoded.Kind != AddressKind.Unknown)
                {
                    _map[targetVA] = decoded;
                    return decoded;
                }
            }
        }

        // Step 6: Section-based classification fallback
        if (_sectionClassifier != null)
        {
            var sectionAnn = _sectionClassifier.Classify(targetVA);
            if (sectionAnn != null)
            {
                _map[targetVA] = sectionAnn;
                return sectionAnn;
            }
        }

        return null;
    }

    public void ExtractIl2CppDefaultsLayout(RegistrationResolver resolver)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var parser = resolver.ElfParser;
        var rodata = parser.FindSectionByName(".rodata");
        var text = parser.FindSectionByName(".text");

        if (rodata == null || text == null) 
        {
            // Cannot scan instructions, but fallback to known layout is still possible
            int ptrSize = parser.PointerSize;
            PopulateDefaultsFromKnownLayout(ptrSize);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"Fallback: populated {Il2CppDefaultsLayout.Count} fields from known layout (ptrSize={ptrSize}, no .rodata/.text)");
            return;
        }

        ulong textStart = text.Value.VirtualAddr;
        ulong textEnd = textStart + text.Value.Size;

        ulong rodataStart = rodata.Value.VirtualAddr;
        ulong rodataEnd = rodataStart + rodata.Value.Size;

        string lastString = "";
        string prevString = "";

        var registerFrequencies = new System.Collections.Generic.Dictionary<int, int>();
        var candidates = new System.Collections.Generic.List<System.Tuple<int, uint, string, string>>();

        int lastStringInstrIndex = -1;
        int instrCount = 0;

        for (ulong instrVa = textStart; instrVa < textEnd - 4; instrVa += 4)
        {
            uint instr = resolver.ReadUInt32(instrVa);
            instrCount++;
            
            // ADRP
            if ((instr & 0x9F000000) == 0x90000000)
            {
                uint rd = instr & 0x1F;
                uint immlo = (instr >> 29) & 3;
                uint immhi = (instr >> 5) & 0x7FFFF;
                int imm = (int)((immhi << 2) | immlo);
                if ((imm & 0x100000) != 0) imm |= unchecked((int)0xFFE00000);
                
                ulong instrPage = instrVa & ~0xFFFUL;
                ulong resultPage = (ulong)((long)instrPage + ((long)imm << 12));

                // If it targets rodata, peek ahead for the ADD to get the exact string VA
                if (resultPage >= rodataStart && resultPage < rodataEnd)
                {
                    for (ulong j = 4; j <= 12; j += 4)
                    {
                        uint nextInstr = resolver.ReadUInt32(instrVa + j);
                        if ((nextInstr & 0xFFC00000) == 0x91000000) // ADD (immediate)
                        {
                            uint addRn = (nextInstr >> 5) & 0x1F;
                            uint addImm = (nextInstr >> 10) & 0xFFF;
                            if (addRn == rd)
                            {
                                ulong stringVa = resultPage + addImm;
                                if (stringVa >= rodataStart && stringVa < rodataEnd)
                                {
                                    string str = ReadNullTerminatedString(resolver, stringVa);
                                    if (!string.IsNullOrEmpty(str) && str.Length > 1 && char.IsLetter(str[0]))
                                    {
                                        prevString = lastString;
                                        lastString = str;
                                        lastStringInstrIndex = instrCount;
                                    }
                                }
                                break;
                            }
                        }
                    }
                }
            }
            // STR (immediate) X0, [Xn, #imm]
            else if ((instr & 0xFFC00000) == 0xF9000000)
            {
                uint rt = instr & 0x1F;
                uint rn = (instr >> 5) & 0x1F;
                uint imm = ((instr >> 10) & 0xFFF) * 8;
                
                if (rt == 0) // Storing X0
                {
                    if (!string.IsNullOrEmpty(lastString) && (instrCount - lastStringInstrIndex) < 20)
                    {
                        if (imm < 0x200 && !lastString.Contains(" ") && lastString.Length < 30 && !lastString.Contains("%s")) 
                        {
                            // Include prevString in the tuple to filter later
                            candidates.Add(new System.Tuple<int, uint, string, string>((int)rn, imm, lastString, prevString));
                            
                            int rnInt = (int)rn;
                            if (!registerFrequencies.ContainsKey(rnInt))
                                registerFrequencies[rnInt] = 0;
                            
                            registerFrequencies[rnInt]++;
                        }
                        lastString = ""; 
                        prevString = "";
                    }
                }
            }
            
            // Early Exit Optimization:
            // If we have found a register with > 40 assignments (the struct is huge),
            // and we haven't seen a string loaded in the last 1000 instructions,
            // we are safely past the initialization block. No need to scan the rest of the megabytes!
            if (instrCount % 1000 == 0)
            {
                int maxF = 0;
                foreach (var v in registerFrequencies.Values) if (v > maxF) maxF = v;
                if (maxF > 40 && (instrCount - lastStringInstrIndex) > 1000)
                {
                    break;
                }
            }
        }

        // Find the base register with the highest frequency. 
        // il2cpp_defaults is a huge struct with ~80 fields initialized sequentially.
        int bestReg = -1;
        int maxFreq = 0;
        foreach (var kvp in registerFrequencies)
        {
            if (kvp.Value > maxFreq)
            {
                maxFreq = kvp.Value;
                bestReg = kvp.Key;
            }
        }

        if (maxFreq < 40)
        {
            // Scanner failed to find enough patterns (e.g. ARM32 binary).
            // Fall back to known layout.
            int ptrSize = parser.PointerSize;
            PopulateDefaultsFromKnownLayout(ptrSize);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"Fallback: populated {Il2CppDefaultsLayout.Count} fields from known layout (ptrSize={ptrSize}, scanner maxFreq={maxFreq})");
            return;
        }
        
        var uniqueOffsets = new System.Collections.Generic.Dictionary<uint, string>();
        
        foreach (var candidate in candidates)
        {
            if (candidate.Item1 == bestReg)
            {
                string currentStr = candidate.Item3;
                string prevStr = candidate.Item4;
                
                bool isSystemNamespace = prevStr == "System" || prevStr == "mscorlib.dll" || prevStr == "mscorlib" || currentStr == "System";
                
                if (isSystemNamespace || !uniqueOffsets.ContainsKey(candidate.Item2))
                {
                    uniqueOffsets[candidate.Item2] = currentStr;
                }
            }
        }
        
        foreach(var kvp in uniqueOffsets)
        {
            Il2CppDefaultsLayout[kvp.Key] = kvp.Value;
        }
        
        sw.Stop();
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"Extracted {Il2CppDefaultsLayout.Count} fields dynamically in {sw.ElapsedMilliseconds}ms");

        // Fallback: if the scanner failed (e.g. ARM32 binary where ADRP doesn't exist),
        // populate from known il2cpp_defaults field order using the correct pointer stride.
        if (Il2CppDefaultsLayout.Count == 0)
        {
            int ptrSize = resolver.ElfParser.PointerSize;
            PopulateDefaultsFromKnownLayout(ptrSize);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"Fallback: populated {Il2CppDefaultsLayout.Count} fields from known layout (ptrSize={ptrSize})");
        }
    }

    private string ReadNullTerminatedString(RegistrationResolver resolver, ulong va)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        ulong currentWordAddr = 0;
        uint currentWord = 0;
        
        for (int i = 0; i < 64; i++) // Max length 64
        {
            ulong addr = va + (ulong)i;
            ulong wordAddr = addr & ~3UL;
            if (wordAddr != currentWordAddr)
            {
                currentWordAddr = wordAddr;
                currentWord = resolver.ReadUInt32(wordAddr);
            }
            int shift = (int)((addr & 3) * 8);
            byte b = (byte)((currentWord >> shift) & 0xFF);
            if (b == 0) break;
            
            // Basic ASCII validation to prevent reading garbage
            if (b < 32 || b > 126) return "";
            bytes.Add(b);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Known il2cpp_defaults struct field layout.
    /// Field indices are architecture-independent; actual offset = field_index × pointer_size.
    /// This table is derived from dynamic extraction on ARM64 binaries and verified
    /// against the il2cpp source (Il2CppDefaults struct in il2cpp-class-internals.h).
    /// Used as fallback when the instruction-pattern scanner fails (e.g. ARM32 binaries).
    /// </summary>
    private static readonly (int fieldIndex, string typeName)[] KnownDefaultsFields = new[]
    {
        (2, "Object"), (3, "Byte"), (4, "Void"), (5, "Boolean"),
        (6, "SByte"), (7, "Int16"), (8, "UInt16"), (9, "Int32"),
        (10, "UInt32"), (11, "IntPtr"), (12, "UIntPtr"), (13, "Int64"),
        (14, "UInt64"), (15, "Single"), (16, "Double"), (17, "Char"),
        (18, "String"), (19, "Enum"), (20, "Array"), (21, "Delegate"),
        (22, "MulticastDelegate"), (23, "AsyncResult"), (24, "ManualResetEvent"),
        (28, "Type"), (29, "MonoType"), (30, "Exception"),
        (32, "Thread"), (33, "InternalThread"), (34, "AppDomain"),
        (35, "AppDomainSetup"), (36, "MemberInfo"), (37, "FieldInfo"),
        (38, "MethodInfo"), (39, "PropertyInfo"), (40, "EventInfo"),
        (41, "StringBuilder"), (42, "StackFrame"), (43, "StackTrace"),
        (45, "TypedReference"),
        (47, "IList`1"), (48, "ICollection`1"), (49, "IEnumerable`1"),
        (50, "IReadOnlyList`1"), (51, "IReadOnlyCollection`1"),
        (52, "RuntimeType"), (53, "Nullable`1"), (54, "System"),
        (55, "Attribute"), (56, "CustomAttributeData"),
        (57, "CustomAttributeTypedArgument"), (58, "CustomAttributeNamedArgument"),
        (59, "Version"), (60, "CultureInfo"), (61, "MonoAsyncCall"),
        (62, "RuntimeAssembly"), (63, "AssemblyName"),
    };

    private void PopulateDefaultsFromKnownLayout(int pointerSize)
    {
        foreach (var (fieldIndex, typeName) in KnownDefaultsFields)
        {
            uint offset = (uint)(fieldIndex * pointerSize);
            Il2CppDefaultsLayout[offset] = typeName;
        }
    }
}
