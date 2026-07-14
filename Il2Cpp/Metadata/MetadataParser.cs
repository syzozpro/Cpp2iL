using Rosetta.Common;
using Rosetta.IO;
using Rosetta.Metadata.Readers;
using Rosetta.Pipeline;

namespace Rosetta.Metadata;

/// <summary>
/// Parses global-metadata.dat by delegating to specialized section readers.
/// Validates header, computes index sizes, then reads all 31 sections.
/// </summary>
public sealed class MetadataParser
{
    private readonly EndianBinaryReader _reader;

    // Parsed state
    public GlobalMetadataHeader Header { get; private set; } = null!;
    public IndexSizeResolver IndexSizes { get; private set; } = null!;
    public StringHeap Strings { get; private set; } = null!;

    /// <summary>
    /// Effective version after sub-version detection.
    /// v24.2+ binaries report version=24 in the header but have v25-era struct layouts.
    /// When detected, this is promoted to 25 so readers skip v24.0-only legacy fields.
    /// </summary>
    public int EffectiveVersion { get; private set; }

    /// <summary>True if v24 binary has byrefTypeIndex in TypeDef (present in v24.2-v24.4, removed in v24.5+).</summary>
    public bool HasByrefTypeIndex { get; private set; }

    // Section arrays
    public TypeDefinition[] TypeDefinitions { get; private set; } = [];
    public MethodDefinition[] MethodDefinitions { get; private set; } = [];
    public FieldDefinition[] FieldDefinitions { get; private set; } = [];
    public ParameterDefinition[] ParameterDefinitions { get; private set; } = [];
    public ImageDefinition[] ImageDefinitions { get; private set; } = [];
    public GenericParameterDef[] GenericParameters { get; private set; } = [];
    public GenericContainerDef[] GenericContainers { get; private set; } = [];
    public PropertyDef[] PropertyDefinitions { get; private set; } = [];
    public EventDef[] EventDefinitions { get; private set; } = [];
    public string[] StringLiterals { get; private set; } = [];
    public ParameterDefaultValueDef[] ParameterDefaultValues { get; private set; } = [];
    public FieldDefaultValueDef[] FieldDefaultValues { get; private set; } = [];
    public byte[] DefaultValuesData { get; private set; } = [];
    public FieldMarshaledSizeDef[] FieldMarshaledSizes { get; private set; } = [];
    public int[] GenericConstraints { get; private set; } = [];
    public int[] NestedTypes { get; private set; } = [];
    public int[] Interfaces { get; private set; } = [];
    public uint[] VTable { get; private set; } = [];
    public InterfaceOffsetPairDef[] InterfaceOffsets { get; private set; } = [];
    public AssemblyDef[] Assemblies { get; private set; } = [];
    public FieldRefDef[] FieldRefs { get; private set; } = [];
    public int[] ReferencedAssemblies { get; private set; } = [];
    public byte[] AttributeData { get; private set; } = [];
    public AttributeDataRangeDef[] AttributeDataRanges { get; private set; } = [];
    public int[] UnresolvedVCallParamTypes { get; private set; } = [];
    public UnresolvedVCallRange[] UnresolvedVCallParamRanges { get; private set; } = [];
    public int[] ExportedTypes { get; private set; } = [];

    /// <summary>
    /// V24 metadataUsageLists: each entry is (pairStart: uint32, pairCount: uint32) = 8 bytes.
    /// Maps method-level usage list index to a range in MetadataUsagePairsRaw.
    /// Empty for V27+.
    /// </summary>
    public byte[] MetadataUsageListsRaw { get; private set; } = [];

    /// <summary>
    /// V24 metadataUsagePairs: each entry is (destinationIndex: uint32, encodedToken: uint32) = 8 bytes.
    /// Maps usage slot index to the encoded metadata token.
    /// Empty for V27+.
    /// </summary>
    public byte[] MetadataUsagePairsRaw { get; private set; } = [];

    /// <summary>
    /// Maximum destinationIndex found in MetadataUsagePairs.
    /// In V24, the metadataUsages binary array extends beyond metadataUsagesCount
    /// to include string literal slots. This property gives the true upper bound.
    /// Returns -1 if no pairs are available.
    /// </summary>
    public int MaxMetadataUsageIndex
    {
        get
        {
            if (MetadataUsagePairsRaw.Length == 0) return -1;
            int max = -1;
            int numPairs = MetadataUsagePairsRaw.Length / 8;
            for (int i = 0; i < numPairs; i++)
            {
                int destIdx = BitConverter.ToInt32(MetadataUsagePairsRaw, i * 8);
                if (destIdx > max) max = destIdx;
            }
            return max;
        }
    }

    public MetadataParser(ReadOnlyMemory<byte> data)
    {
        _reader = new EndianBinaryReader(data);
    }

    /// <summary>Full parse pipeline: header → index sizes → all sections → string resolution.</summary>
    public void Parse()
    {
        ParseHeader();
        IndexSizes = new IndexSizeResolver(Header);
        Strings = new StringHeap(_reader, Header.Sections[GlobalMetadataHeader.MetadataStrings]);

        var s = Header.Sections;
        var idx = IndexSizes;
        int ver = Header.Version;

        // ── V24 sub-version auto-detection ──────────────────────────────────
        // V24 header can mean v24.0 through v24.5. The struct layouts differ:
        //   v24.0:  MethodDef has customAttrIdx + 5 legacy fields → stride 56
        //   v24.2+: MethodDef matches v25 layout (no legacy fields) → stride 32
        //
        // Since we only have the integer version, detect by stride divisibility.
        // If v24.2+, promote to 25 so all "version <= 24" checks skip legacy fields.
        if (ver == 24)
        {
            var methodSection = s[GlobalMetadataHeader.Methods];
            int v25Stride = MethodDefinitionReader.ComputeStride(idx, 25);
            int v24Stride = MethodDefinitionReader.ComputeStride(idx, 24);

            // If v25 stride divides evenly, treat as v24.2+.
            // When both v24 and v25 strides divide evenly (ambiguous), v24.2+ is
            // overwhelmingly more common in practice, so prefer it.
            if (methodSection.Size > 0 && methodSection.Size % v25Stride == 0)
            {
                ConsoleReporter.Info($"  V24 sub-version detected: v24.2+ (method stride={v25Stride}, not {v24Stride})");
                ver = 25; // Treat as v25 for all readers — same struct layout

                // TypeDef: v24.2 still has byrefTypeIndex (removed in v24.5).
                // Check TypeDef section: stride 92 = with byref, 88 = without.
                var tdSection = s[GlobalMetadataHeader.TypeDefinitions];
                int strideWithByref = TypeDefinitionReader.ComputeStride(idx, 25) + 4; // +4 for byrefTypeIndex
                int strideWithout = TypeDefinitionReader.ComputeStride(idx, 25);
                if (tdSection.Size > 0 && tdSection.Size % strideWithByref == 0 &&
                    tdSection.Size % strideWithout != 0)
                {
                    HasByrefTypeIndex = true;
                    ConsoleReporter.Info($"  V24 TypeDef: byrefTypeIndex present (stride={strideWithByref})");
                }
            }
        }

        EffectiveVersion = ver;

        // Core definitions
        TypeDefinitions = TypeDefinitionReader.Read(_reader, s[GlobalMetadataHeader.TypeDefinitions], idx, ver, HasByrefTypeIndex);
        MethodDefinitions = MethodDefinitionReader.Read(_reader, s[GlobalMetadataHeader.Methods], idx, ver);
        FieldDefinitions = FieldParameterReader.ReadFields(_reader, s[GlobalMetadataHeader.Fields], idx, ver);
        ParameterDefinitions = FieldParameterReader.ReadParameters(_reader, s[GlobalMetadataHeader.Parameters], idx, ver);
        ImageDefinitions = ImageReader.Read(_reader, s[GlobalMetadataHeader.Images], idx, ver);

        // Generics
        GenericParameters = GenericsReader.ReadParameters(_reader, s[GlobalMetadataHeader.GenericParameters], idx);
        GenericConstraints = GenericsReader.ReadConstraints(_reader, s[GlobalMetadataHeader.GenericConstraints], idx);
        GenericContainers = GenericsReader.ReadContainers(_reader, s[GlobalMetadataHeader.GenericContainers], ver);

        // Properties & Events
        PropertyDefinitions = PropertyEventReader.ReadProperties(_reader, s[GlobalMetadataHeader.Properties], idx, ver);
        EventDefinitions = PropertyEventReader.ReadEvents(_reader, s[GlobalMetadataHeader.Events], idx, ver);

        // String literals
        StringLiterals = StringLiteralReader.Read(_reader, s[GlobalMetadataHeader.StringLiteralOffsets], s[GlobalMetadataHeader.StringLiteralData], ver);

        // Default values
        ParameterDefaultValues = DefaultValueReader.ReadParamDefaults(_reader, s[GlobalMetadataHeader.ParameterDefaultValues], idx);
        FieldDefaultValues = DefaultValueReader.ReadFieldDefaults(_reader, s[GlobalMetadataHeader.FieldDefaultValues], idx);
        DefaultValuesData = DefaultValueReader.ReadDefaultValuesBlob(_reader, s[GlobalMetadataHeader.DefaultValuesData]);
        FieldMarshaledSizes = DefaultValueReader.ReadMarshaledSizes(_reader, s[GlobalMetadataHeader.FieldMarshaledSizes], idx);

        // VTable & interfaces
        NestedTypes = VTableReader.ReadNestedTypes(_reader, s[GlobalMetadataHeader.NestedTypes], idx);
        Interfaces = VTableReader.ReadInterfaces(_reader, s[GlobalMetadataHeader.Interfaces], idx);
        VTable = VTableReader.ReadVTable(_reader, s[GlobalMetadataHeader.VTableMethods]);
        InterfaceOffsets = VTableReader.ReadInterfaceOffsets(_reader, s[GlobalMetadataHeader.InterfaceOffsets], idx);

        // Assemblies
        Assemblies = AssemblyReader.Read(_reader, s[GlobalMetadataHeader.Assemblies], ver, Header.Version);
        FieldRefs = AssemblyReader.ReadFieldRefs(_reader, s[GlobalMetadataHeader.FieldRefs], idx);
        ReferencedAssemblies = AssemblyReader.ReadReferencedAssemblies(_reader, s[GlobalMetadataHeader.ReferencedAssemblies]);

        // Attributes & misc
        AttributeData = MiscSectionReader.ReadAttributeData(_reader, s[GlobalMetadataHeader.AttributeData]);
        AttributeDataRanges = MiscSectionReader.ReadAttributeRanges(_reader, s[GlobalMetadataHeader.AttributeDataRanges], ver);
        UnresolvedVCallParamTypes = MiscSectionReader.ReadUnresolvedVCallParamTypes(_reader, s[GlobalMetadataHeader.UnresolvedVCParamTypes], idx);
        UnresolvedVCallParamRanges = MiscSectionReader.ReadUnresolvedVCallRanges(_reader, s[GlobalMetadataHeader.UnresolvedVCParamRanges]);
        ExportedTypes = MiscSectionReader.ReadExportedTypes(_reader, s[GlobalMetadataHeader.ExportedTypes]);

        // Resolve all string names
        ResolveNames();
    }

    /// <summary>Read a metadata string by index (for external callers).</summary>
    public string ReadMetadataString(int index) => Strings.Read(index);

    // ==================================================================
    // Header parsing — supports v24-v29 (legacy) and v39 (modern)
    // ==================================================================
    private void ParseHeader()
    {
        _reader.Position = 0;

        uint magic = _reader.ReadUInt32();
        if (magic != Constants.MetadataMagic)
            throw new InvalidDataException($"Invalid metadata magic: 0x{magic:X8}");

        int version = _reader.ReadInt32();
        if (!Array.Exists(Constants.SupportedVersions, v => v == version))
            throw new InvalidDataException(
                $"Unsupported metadata version: {version}. Supported: {string.Join(", ", Constants.SupportedVersions)}");

        if (version >= 38) // v38+ uses 12-byte section headers (offset+size+count)
            ParseHeaderV39(magic, version);
        else
            ParseHeaderLegacy(magic, version); // v23-v35 uses 8-byte section headers (offset+size)
    }

    /// <summary>V38+: 12-byte section table (offset + size + itemCount) × N sections.</summary>
    /// <remarks>
    /// V38-V39: 31 sections. V104+: 32 sections (adds typeInlineArrays).
    /// The section count is auto-detected from the first section offset.
    ///
    /// CRITICAL: In v104+, typeInlineArrays is inserted at physical index 20
    /// (after typeDefinitions), pushing Images/Assemblies/etc. up by one.
    /// We remap physical→logical so downstream code always uses the same
    /// logical constants (Images=20, Assemblies=21, ...) regardless of version.
    /// </remarks>
    private void ParseHeaderV39(uint magic, int version)
    {
        int firstSectionOffset = _reader.PeekInt32At(8);

        if (firstSectionOffset < Constants.MetadataHeaderBaseSize)
            throw new InvalidDataException(
                $"Metadata appears corrupted or encrypted: firstSectionOffset ({firstSectionOffset}) " +
                $"is smaller than header base size ({Constants.MetadataHeaderBaseSize}).");

        int sectionCount = (firstSectionOffset - Constants.MetadataHeaderBaseSize) / Constants.SectionHeaderEntrySize_V39;

        if (sectionCount < 0 || sectionCount > 1000)
            throw new InvalidDataException(
                $"Metadata appears corrupted or encrypted: computed section count ({sectionCount}) is out of range.");

        // v104+ has 32 physical sections with typeInlineArrays at physical[20].
        // v38-v39 has 31 physical sections with no typeInlineArrays.
        bool hasTypeInlineArrays = version >= 104 && sectionCount >= 32;

        var sections = new MetadataSectionHeader[GlobalMetadataHeader.LogicalSectionCount];
        for (int phys = 0; phys < Math.Min(sectionCount, GlobalMetadataHeader.LogicalSectionCount); phys++)
        {
            int offset = _reader.ReadInt32();
            int size = _reader.ReadInt32();
            int itemCount = _reader.ReadInt32();
            var header = new MetadataSectionHeader(offset, size, itemCount);

            // Remap physical → logical index
            int logical;
            if (!hasTypeInlineArrays)
            {
                logical = phys; // v38-v39: 1:1 mapping
            }
            else if (phys < 20)
            {
                logical = phys; // sections 0-19: same in all versions
            }
            else if (phys == 20)
            {
                logical = GlobalMetadataHeader.TypeInlineArrays; // physical[20] → logical[31]
            }
            else
            {
                logical = phys - 1; // physical[21..31] → logical[20..30] (shifted back)
            }

            if (logical >= 0 && logical < sections.Length)
                sections[logical] = header;
        }

        Header = new GlobalMetadataHeader { Magic = magic, Version = version, Sections = sections };
    }

    /// <summary>
    /// V23-V35: Named offset+size pairs (8 bytes each).
    /// Reads fields in the order defined by Il2CppGlobalMetadataHeader and maps them
    /// to our logical section indices.
    /// </summary>
    private void ParseHeaderLegacy(uint magic, int version)
    {
        // Allocate all logical slots (unused ones stay at default 0,0,0)
        var sections = new MetadataSectionHeader[GlobalMetadataHeader.LogicalSectionCount];

        // Helper: read offset+size pair into a logical section slot
        void ReadSection(int logicalIndex)
        {
            int offset = _reader.ReadInt32();
            int size = _reader.ReadInt32();
            sections[logicalIndex] = new MetadataSectionHeader(offset, size, 0); // itemCount computed later
        }

        // Helper: skip offset+size pair (version-specific fields we don't need)
        void SkipSection() { _reader.ReadInt32(); _reader.ReadInt32(); }

        // Read in the exact order of Il2CppGlobalMetadataHeader fields:
        ReadSection(GlobalMetadataHeader.StringLiteralOffsets);   // stringLiteralOffset/Size
        ReadSection(GlobalMetadataHeader.StringLiteralData);      // stringLiteralDataOffset/Size
        ReadSection(GlobalMetadataHeader.MetadataStrings);        // stringOffset/Size (metadata strings)
        ReadSection(GlobalMetadataHeader.Events);                 // eventsOffset/Size
        ReadSection(GlobalMetadataHeader.Properties);             // propertiesOffset/Size
        ReadSection(GlobalMetadataHeader.Methods);                // methodsOffset/Size
        ReadSection(GlobalMetadataHeader.ParameterDefaultValues); // parameterDefaultValuesOffset/Size
        ReadSection(GlobalMetadataHeader.FieldDefaultValues);     // fieldDefaultValuesOffset/Size
        ReadSection(GlobalMetadataHeader.DefaultValuesData);      // fieldAndParameterDefaultValueDataOffset/Size
        ReadSection(GlobalMetadataHeader.FieldMarshaledSizes);    // fieldMarshaledSizesOffset/Size
        ReadSection(GlobalMetadataHeader.Parameters);             // parametersOffset/Size
        ReadSection(GlobalMetadataHeader.Fields);                 // fieldsOffset/Size
        ReadSection(GlobalMetadataHeader.GenericParameters);      // genericParametersOffset/Size
        ReadSection(GlobalMetadataHeader.GenericConstraints);     // genericParameterConstraintsOffset/Size
        ReadSection(GlobalMetadataHeader.GenericContainers);      // genericContainersOffset/Size
        ReadSection(GlobalMetadataHeader.NestedTypes);            // nestedTypesOffset/Size
        ReadSection(GlobalMetadataHeader.Interfaces);             // interfacesOffset/Size
        ReadSection(GlobalMetadataHeader.VTableMethods);          // vtableMethodsOffset/Size
        ReadSection(GlobalMetadataHeader.InterfaceOffsets);       // interfaceOffsetsOffset/Size
        ReadSection(GlobalMetadataHeader.TypeDefinitions);        // typeDefinitionsOffset/Size

        // ── V24 sub-version detection for header layout ─────────────────────
        // rgctxEntries: present in v23 and v24.0-v24.15, REMOVED in v24.2+ (moved to binary)
        // metadataUsageLists/Pairs: present in v23-v24.4, REMOVED in v24.5+
        //
        // Detect v24 sub-version from the Methods section stride:
        //   v24.0 stride = 56 (has customAttr + legacy fields)
        //   v24.2+ stride = 32 (same as v25+)
        bool isV24_2Plus = false;
        bool isV24_5Plus = false;
        if (version == 24)
        {
            int methodSize = sections[GlobalMetadataHeader.Methods].Size;
            if (methodSize > 0 && methodSize % 32 == 0)
            {
                if (methodSize % 56 != 0)
                {
                    // Unambiguous: only stride 32 works → v24.2+
                    isV24_2Plus = true;
                }
                else
                {
                    // Ambiguous: divisible by both 32 and 56.
                    // Heuristic: if this were v24.0, the next 8 bytes at the reader
                    // position would be rgctxEntries (offset + size). Check if those
                    // values make sense as a section header. If not, it's v24.2+.
                    int savedPos = _reader.Position;
                    int rgctxOffset = _reader.PeekInt32At(savedPos);
                    int rgctxSize = _reader.PeekInt32At(savedPos + 4);
                    // v24.0 rgctxEntries: offset must be within file and size non-negative
                    bool looksLikeSection = rgctxOffset >= 0 && rgctxSize >= 0
                        && rgctxOffset + rgctxSize <= _reader.Length
                        && rgctxOffset < _reader.Length;
                    if (!looksLikeSection)
                    {
                        isV24_2Plus = true;
                    }
                    else
                    {
                        // Further validation: if v24.0, skipping rgctxEntries should land on
                        // Images section. Read its offset and check it's reasonable.
                        int imagesOffsetIfV24_0 = _reader.PeekInt32At(savedPos + 8);
                        int imagesSizeIfV24_0 = _reader.PeekInt32At(savedPos + 12);
                        // Images directly without rgctxEntries skip (v24.2+ path)
                        int imagesOffsetIfV24_2 = _reader.PeekInt32At(savedPos);
                        int imagesSizeIfV24_2 = _reader.PeekInt32At(savedPos + 4);

                        // v24.2+ Image stride = 40 (with customAttrs), v24.0 = 32
                        bool v24_0_valid = imagesSizeIfV24_0 > 0 && imagesSizeIfV24_0 % 32 == 0
                            && imagesOffsetIfV24_0 > 0 && imagesOffsetIfV24_0 < _reader.Length;
                        bool v24_2_valid = imagesSizeIfV24_2 > 0 && imagesSizeIfV24_2 % 40 == 0
                            && imagesOffsetIfV24_2 > 0 && imagesOffsetIfV24_2 < _reader.Length;

                        if (v24_2_valid && !v24_0_valid)
                            isV24_2Plus = true;
                        else if (!v24_2_valid && v24_0_valid)
                            isV24_2Plus = false; // stays v24.0
                        // If both look valid or both invalid, fall back to stride ratio heuristic:
                        // v24.2+ gives more methods (methodSize/32 vs /56). Real games have >100 methods.
                        else
                            isV24_2Plus = (methodSize / 32) > (methodSize / 56);
                    }
                }
            }

            if (isV24_2Plus)
            {
                // Further detect v24.5: TypeDef stride 88 (no byref) vs 92 (with byref = v24.2-v24.4)
                int tdSize = sections[GlobalMetadataHeader.TypeDefinitions].Size;
                // v24.5 has TypeDef stride 88 (same as v27), v24.2-v24.4 has stride 92
                isV24_5Plus = (tdSize > 0 && tdSize % 88 == 0 && tdSize % 92 != 0);
            }
        }

        // rgctxEntries: v23 and v24.0-v24.15 only (NOT in v24.2+, NOT in v25+)
        if (version <= 24 && !isV24_2Plus)
            SkipSection(); // rgctxEntriesOffset/Count

        ReadSection(GlobalMetadataHeader.Images);                 // imagesOffset/Size
        ReadSection(GlobalMetadataHeader.Assemblies);             // assembliesOffset/Size

        // metadataUsageLists/Pairs: present in v23-v24.4, removed in v24.5+ and v27+
        // These tables map .bss metadata variable indices to encoded tokens.
        if (version <= 24 && !isV24_5Plus)
        {
            int listsOffset = _reader.ReadInt32();
            int listsSize = _reader.ReadInt32();
            int pairsOffset = _reader.ReadInt32();
            int pairsSize = _reader.ReadInt32();

            // Save the header read position (next field in the header sequence)
            int savedPosition = _reader.Position;

            // Store raw bytes for later processing
            if (listsSize > 0)
            {
                _reader.Position = listsOffset;
                MetadataUsageListsRaw = _reader.ReadBytes(listsSize).ToArray();
                ConsoleReporter.Info($"  MetadataUsageLists: {listsSize / 8} entries");
            }
            if (pairsSize > 0)
            {
                _reader.Position = pairsOffset;
                MetadataUsagePairsRaw = _reader.ReadBytes(pairsSize).ToArray();
                ConsoleReporter.Info($"  MetadataUsagePairs: {pairsSize / 8} entries");
            }

            // Restore header position for subsequent ReadSection calls
            _reader.Position = savedPosition;
        }

        ReadSection(GlobalMetadataHeader.FieldRefs);              // fieldRefsOffset/Size
        ReadSection(GlobalMetadataHeader.ReferencedAssemblies);   // referencedAssembliesOffset/Size

        // v23-v27.2: attributesInfo + attributeTypes (replaced by attributeData in v29+)
        if (version < 29)
        {
            ReadSection(GlobalMetadataHeader.AttributeDataRanges); // attributesInfoOffset/Count → map to ranges
            ReadSection(GlobalMetadataHeader.AttributeData);       // attributeTypesOffset/Count → map to data
        }

        // v29+: attributeData + attributeDataRange
        if (version >= 29)
        {
            ReadSection(GlobalMetadataHeader.AttributeData);       // attributeDataOffset/Size
            ReadSection(GlobalMetadataHeader.AttributeDataRanges); // attributeDataRangeOffset/Size
        }

        ReadSection(GlobalMetadataHeader.UnresolvedVCParamTypes);  // unresolvedVirtualCallParameterTypesOffset/Size
        ReadSection(GlobalMetadataHeader.UnresolvedVCParamRanges); // unresolvedVirtualCallParameterRangesOffset/Size

        // v23+: windowsRuntimeTypeNames
        ReadSection(GlobalMetadataHeader.WinRTTypeNamePairs);      // windowsRuntimeTypeNamesOffset/Size

        // v27+: windowsRuntimeStrings
        if (version >= 27)
            ReadSection(GlobalMetadataHeader.WinRTStrings);        // windowsRuntimeStringsOffset/Size

        // v24+: exportedTypeDefinitions
        if (version >= 24)
            ReadSection(GlobalMetadataHeader.ExportedTypes);       // exportedTypeDefinitionsOffset/Size

        Header = new GlobalMetadataHeader { Magic = magic, Version = version, Sections = sections };
    }

    // ==================================================================
    // String resolution for all named definitions
    // ==================================================================
    private void ResolveNames()
    {
        foreach (var td in TypeDefinitions)
        {
            td.Name = Strings.Read(td.NameIndex);
            td.Namespace = Strings.Read(td.NamespaceIndex);
        }
        foreach (var md in MethodDefinitions)
            md.Name = Strings.Read(md.NameIndex);
        foreach (var fd in FieldDefinitions)
            fd.Name = Strings.Read(fd.NameIndex);
        foreach (var pd in ParameterDefinitions)
            pd.Name = Strings.Read(pd.NameIndex);
        for (int i = 0; i < ImageDefinitions.Length; i++)
            ImageDefinitions[i].Name = Strings.Read(ImageDefinitions[i].NameIndex);
        foreach (var gp in GenericParameters)
            gp.Name = Strings.Read(gp.NameIndex);
        foreach (var prop in PropertyDefinitions)
            prop.Name = Strings.Read(prop.NameIndex);
        foreach (var evt in EventDefinitions)
            evt.Name = Strings.Read(evt.NameIndex);
        foreach (var asm in Assemblies)
            asm.Name = Strings.Read(asm.NameIndex);
    }

    public void ClearTempMemory()
    {
        _reader.Clear();
        Strings = null!;
    }

    public void ClearPostAssemblyMemory()
    {
        MetadataUsageListsRaw = Array.Empty<byte>();
        MetadataUsagePairsRaw = Array.Empty<byte>();

        GenericParameters = Array.Empty<GenericParameterDef>();
        GenericConstraints = Array.Empty<int>();
        GenericContainers = Array.Empty<GenericContainerDef>();
        VTable = Array.Empty<uint>();
        InterfaceOffsets = Array.Empty<InterfaceOffsetPairDef>();
        Assemblies = Array.Empty<AssemblyDef>();
        FieldRefs = Array.Empty<FieldRefDef>();
        ReferencedAssemblies = Array.Empty<int>();
        AttributeData = Array.Empty<byte>();
        AttributeDataRanges = Array.Empty<AttributeDataRangeDef>();
        UnresolvedVCallParamTypes = Array.Empty<int>();
        UnresolvedVCallParamRanges = Array.Empty<UnresolvedVCallRange>();
        ExportedTypes = Array.Empty<int>();
    }

    public void ClearAll()
    {
        ClearTempMemory();
        ClearPostAssemblyMemory();
        TypeDefinitions = Array.Empty<TypeDefinition>();
        MethodDefinitions = Array.Empty<MethodDefinition>();
        FieldDefinitions = Array.Empty<FieldDefinition>();
        ParameterDefinitions = Array.Empty<ParameterDefinition>();
        ImageDefinitions = Array.Empty<ImageDefinition>();
        PropertyDefinitions = Array.Empty<PropertyDef>();
        EventDefinitions = Array.Empty<EventDef>();
        StringLiterals = Array.Empty<string>();
        ParameterDefaultValues = Array.Empty<ParameterDefaultValueDef>();
        FieldDefaultValues = Array.Empty<FieldDefaultValueDef>();
        DefaultValuesData = Array.Empty<byte>();
        FieldMarshaledSizes = Array.Empty<FieldMarshaledSizeDef>();
        NestedTypes = Array.Empty<int>();
        Interfaces = Array.Empty<int>();
    }
}
