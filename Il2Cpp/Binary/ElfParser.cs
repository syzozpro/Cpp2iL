using Rosetta.Common;
using Rosetta.IO;
using Rosetta.Pipeline;

namespace Rosetta.Binary;

/// <summary>
/// ELF parser for libil2cpp.so — supports both ELF32 and ELF64.
/// Extracts section headers, program headers, and symbol tables.
/// Source: ELF specification + Native Toolchain Omnibus §11 (output format).
/// </summary>
public sealed class ElfParser : IBinaryParser
{
    private readonly EndianBinaryReader _reader;

    // ELF header fields
    public byte ElfClass { get; private set; }      // 1=32-bit, 2=64-bit
    public byte ElfData { get; private set; }       // 1=LE, 2=BE
    public ushort Machine { get; private set; }     // 0xB7=AArch64, 0x28=ARM, 0x03=x86, 0x3E=x86_64
    public ulong EntryPoint { get; private set; }

    /// <summary>True if this is a 32-bit ELF binary (ElfClass == 1).</summary>
    public bool Is32Bit => ElfClass == 1;

    /// <summary>Pointer size in bytes (4 for 32-bit, 8 for 64-bit).</summary>
    public int PointerSize => Is32Bit ? 4 : 8;

    public string ArchitectureName => Machine switch
    {
        0x03 => "x86",
        0x3E => "x86_64",
        0x28 => "ARM",
        0xB7 => "AArch64",
        _ => $"Unknown (0x{Machine:X2})"
    };

    public bool IsX64 => Machine == 0x3E;

    // Parsed structures
    public BinarySectionHeader[] SectionHeaders { get; private set; } = [];
    public BinarySegment[] ProgramHeaders { get; private set; } = [];
    public BinarySymbol[] DynSymbols { get; private set; } = [];

    // Key sections (resolved by name)
    public BinarySectionHeader? DataSection { get; private set; }
    public BinarySectionHeader? RoDataSection { get; private set; }
    public BinarySectionHeader? BssSection { get; private set; }
    public BinarySectionHeader? DynStrSection { get; private set; }
    public BinarySectionHeader? DynSymSection { get; private set; }
    public BinarySectionHeader? RelaPltSection { get; private set; }
    public BinarySectionHeader? RelPltSection { get; private set; }
    public BinarySectionHeader? GotSection { get; private set; }
    public BinarySectionHeader? RelaDynSection { get; private set; }
    public BinarySectionHeader? RelDynSection { get; private set; }
    public BinarySectionHeader? GccExceptTableSection { get; private set; }

    public Dictionary<ulong, string> PltGotSymbols { get; private set; } = new();
    public Dictionary<ulong, ulong> RelocationMap { get; private set; } = new();

    /// <summary>
    /// PLT entry VA → symbol name. Built by decoding each PLT stub's ADRP+LDR
    /// to derive the GOT slot, then looking it up in PltGotSymbols.
    ///
    /// This is needed because BL instructions target PLT entry VAs (not GOT VAs),
    /// and ELF .dynsym symbols for imports have Value=0 (lazy binding).
    /// Only populated for ARM64 binaries.
    /// </summary>
    public Dictionary<ulong, string> PltStubSymbols { get; private set; } = new();

    public ElfParser(ReadOnlyMemory<byte> data)
    {
        _reader = new EndianBinaryReader(data);
    }

    public void Parse()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"ElfParser.Parse()");
        ValidateAndParseHeader();
        ParseSectionHeaders();
        ResolveSectionNames();
        LocateKeySections();
        ParseDynSymbols();
        ParseRelaPlt();
        // PLT stub resolution: decode each PLT entry to map stub VA → symbol name
        if (Is32Bit)
            ParsePltStubs32();
        else
            ParsePltStubs();

        // Parse relocations to build map
        BuildRelocationMap();
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  ElfParser: Parse complete. {SectionHeaders.Length} sections, {ProgramHeaders.Length} program headers ({(Is32Bit ? "32-bit" : "64-bit")})");
    }

    // ====================================================================
    // STEP 1: ELF Header
    //
    // ELF32 header: 52 bytes
    //   e_ident[16] + e_type(2) + e_machine(2) + e_version(4)
    //   + e_entry(4) + e_phoff(4) + e_shoff(4) + e_flags(4) + e_ehsize(2)
    //   + e_phentsize(2) + e_phnum(2) + e_shentsize(2) + e_shnum(2) + e_shstrndx(2)
    //
    // ELF64 header: 64 bytes
    //   Same but e_entry/e_phoff/e_shoff are 8 bytes each.
    // ====================================================================

    private void ValidateAndParseHeader()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    ElfParser.ValidateAndParseHeader()");
        _reader.Position = 0;

        // e_ident[0..3]: Magic
        uint magic = _reader.ReadUInt32();
        if (magic != Constants.ElfMagic)
            throw new InvalidDataException($"Invalid ELF magic: 0x{magic:X8}");

        // e_ident[4]: class (1=32-bit, 2=64-bit)
        ElfClass = _reader.ReadByte();
        if (ElfClass != 1 && ElfClass != 2)
            throw new InvalidDataException($"Unsupported ELF class={ElfClass} (expected 1=32-bit or 2=64-bit)");

        // e_ident[5]: data encoding (must be 1 = little-endian)
        ElfData = _reader.ReadByte();
        if (ElfData != 1)
            throw new InvalidDataException($"Expected little-endian (data=1), got data={ElfData}");

        // Skip rest of e_ident (bytes 6-15)
        _reader.Position = 16;

        // e_type (2), e_machine (2)
        _reader.ReadUInt16(); // e_type
        Machine = _reader.ReadUInt16();

        // Validate machine type
        if (Machine != 0xB7 && Machine != 0x28 && Machine != 0x03 && Machine != 0x3E)
            throw new InvalidDataException($"Unsupported machine=0x{Machine:X4} (expected ARM64=0xB7, ARM32=0x28, x86=0x03, or x86_64=0x3E)");

        _reader.ReadUInt32(); // e_version

        if (Is32Bit)
        {
            EntryPoint = _reader.ReadUInt32();            // e_entry (4)
            ulong phOffset = _reader.ReadUInt32();        // e_phoff (4)
            ulong shOffset = _reader.ReadUInt32();        // e_shoff (4)
            _reader.ReadUInt32();                         // e_flags
            _reader.ReadUInt16();                         // e_ehsize
            ushort phEntrySize = _reader.ReadUInt16();    // e_phentsize
            ushort phCount = _reader.ReadUInt16();        // e_phnum
            ushort shEntrySize = _reader.ReadUInt16();    // e_shentsize
            ushort shCount = _reader.ReadUInt16();        // e_shnum
            ushort shStrIndex = _reader.ReadUInt16();     // e_shstrndx

            _phOffset = phOffset;
            _shOffset = shOffset;
            _phEntrySize = phEntrySize;
            _phCount = phCount;
            _shEntrySize = shEntrySize;
            _shCount = shCount;
            _shStrIndex = shStrIndex;
        }
        else
        {
            EntryPoint = _reader.ReadUInt64();              // e_entry (8)
            ulong phOffset = _reader.ReadUInt64();          // e_phoff (8)
            ulong shOffset = _reader.ReadUInt64();          // e_shoff (8)
            _reader.ReadUInt32();                           // e_flags
            _reader.ReadUInt16();                           // e_ehsize
            ushort phEntrySize = _reader.ReadUInt16();      // e_phentsize
            ushort phCount = _reader.ReadUInt16();          // e_phnum
            ushort shEntrySize = _reader.ReadUInt16();      // e_shentsize
            ushort shCount = _reader.ReadUInt16();          // e_shnum
            ushort shStrIndex = _reader.ReadUInt16();       // e_shstrndx

            _phOffset = phOffset;
            _shOffset = shOffset;
            _phEntrySize = phEntrySize;
            _phCount = phCount;
            _shEntrySize = shEntrySize;
            _shCount = shCount;
            _shStrIndex = shStrIndex;
        }

        string archName = Machine switch
        {
            0x28 => "ARM32",
            0xB7 => "ARM64",
            0x03 => "x86",
            0x3E => "x86_64",
            _    => $"0x{Machine:X4}"
        };
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  ELF: {(Is32Bit ? "32-bit" : "64-bit")} {archName}");
    }

    private ulong _phOffset, _shOffset;
    private ushort _phEntrySize, _phCount, _shEntrySize, _shCount, _shStrIndex;

    private const uint R_AARCH64_RELATIVE = 0x403;
    private const uint R_ARM_RELATIVE     = 0x17;
    private const uint R_386_RELATIVE     = 0x08;
    private const uint R_X86_64_RELATIVE  = 0x08;

    private void BuildRelocationMap()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    ElfParser.BuildRelocationMap()");

        if (Is32Bit)
            BuildRelocationMap32();
        else
            BuildRelocationMap64();
    }

    private void BuildRelocationMap64()
    {
        var relaDyn = RelaDynSection;
        if (relaDyn is null) return;

        int relaOff = (int)relaDyn.Value.FileOffset;
        int relaSize = (int)relaDyn.Value.Size;
        int relaCount = relaSize / 24;

        for (int i = 0; i < relaCount; i++)
        {
            _reader.Position = relaOff + i * 24;
            ulong rOffset = _reader.ReadUInt64();
            ulong rInfo = _reader.ReadUInt64();
            long rAddend = _reader.ReadInt64();

            uint rType = (uint)(rInfo & 0xFFFFFFFF);

            if ((rType == R_AARCH64_RELATIVE || rType == R_X86_64_RELATIVE) && rAddend > 0)
            {
                RelocationMap[rOffset] = (ulong)rAddend;
            }
        }
    }

    private void BuildRelocationMap32()
    {
        var relaSection = RelaDynSection;
        var relSection = RelDynSection;

        if (relaSection is not null)
        {
            int off = (int)relaSection.Value.FileOffset;
            int size = (int)relaSection.Value.Size;
            int count = size / 12;

            for (int i = 0; i < count; i++)
            {
                _reader.Position = off + i * 12;
                ulong rOffset = _reader.ReadUInt32();
                uint rInfo = _reader.ReadUInt32();
                int rAddend = _reader.ReadInt32();

                uint rType = rInfo & 0xFF;

                if ((rType == R_ARM_RELATIVE || rType == R_386_RELATIVE) && rAddend > 0)
                    RelocationMap[rOffset] = (ulong)rAddend;
            }
        }
        else if (relSection is not null)
        {
            int off = (int)relSection.Value.FileOffset;
            int size = (int)relSection.Value.Size;
            int count = size / 8;

            for (int i = 0; i < count; i++)
            {
                _reader.Position = off + i * 8;
                ulong rOffset = _reader.ReadUInt32();
                uint rInfo = _reader.ReadUInt32();

                uint rType = rInfo & 0xFF;

                if (rType == R_ARM_RELATIVE || rType == R_386_RELATIVE)
                {
                    long fileOff = VirtualToFileOffset(rOffset);
                    if (fileOff > 0 && fileOff + 4 <= _reader.Length)
                    {
                        _reader.Position = (int)fileOff;
                        uint addend = _reader.ReadUInt32();
                        if (addend > 0)
                            RelocationMap[rOffset] = addend;
                    }
                }
            }
        }
    }

    // ====================================================================
    // STEP 2: Section Headers + Program Headers
    //
    // ELF32 section header = 40 bytes:
    //   name(4) type(4) flags(4) addr(4) offset(4) size(4) link(4) info(4) addralign(4) entsize(4)
    //
    // ELF64 section header = 64 bytes:
    //   name(4) type(4) flags(8) addr(8) offset(8) size(8) link(4) info(4) addralign(8) entsize(8)
    //
    // ELF32 program header = 32 bytes:
    //   type(4) offset(4) vaddr(4) paddr(4) filesz(4) memsz(4) flags(4) align(4)
    //   NOTE: flags is at position 24 in ELF32, but at position 4 in ELF64!
    //
    // ELF64 program header = 56 bytes:
    //   type(4) flags(4) offset(8) vaddr(8) paddr(8) filesz(8) memsz(8) align(8)
    // ====================================================================

    private void ParseSectionHeaders()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    ElfParser.ParseSectionHeaders()");
        var headers = new BinarySectionHeader[_shCount];

        for (int i = 0; i < _shCount; i++)
        {
            _reader.Position = (int)(_shOffset + (ulong)(i * _shEntrySize));

            if (Is32Bit)
            {
                uint nameOffset = _reader.ReadUInt32();
                uint type       = _reader.ReadUInt32();
                ulong flags     = _reader.ReadUInt32();   // 4 bytes in ELF32
                ulong vaddr     = _reader.ReadUInt32();
                ulong offset    = _reader.ReadUInt32();
                ulong size      = _reader.ReadUInt32();
                uint link       = _reader.ReadUInt32();
                uint info       = _reader.ReadUInt32();
                ulong addralign = _reader.ReadUInt32();
                ulong entsize   = _reader.ReadUInt32();

                headers[i] = new BinarySectionHeader(nameOffset, type, flags, vaddr, offset, size, link, info, addralign, entsize);
            }
            else
            {
                uint nameOffset = _reader.ReadUInt32();
                uint type       = _reader.ReadUInt32();
                ulong flags     = _reader.ReadUInt64();   // 8 bytes in ELF64
                ulong vaddr     = _reader.ReadUInt64();
                ulong offset    = _reader.ReadUInt64();
                ulong size      = _reader.ReadUInt64();
                uint link       = _reader.ReadUInt32();
                uint info       = _reader.ReadUInt32();
                ulong addralign = _reader.ReadUInt64();
                ulong entsize   = _reader.ReadUInt64();

                headers[i] = new BinarySectionHeader(nameOffset, type, flags, vaddr, offset, size, link, info, addralign, entsize);
            }
        }

        // Parse program headers
        var phHeaders = new BinarySegment[_phCount];
        for (int i = 0; i < _phCount; i++)
        {
            _reader.Position = (int)(_phOffset + (ulong)(i * _phEntrySize));

            if (Is32Bit)
            {
                // ELF32 Phdr: type(4) offset(4) vaddr(4) paddr(4) filesz(4) memsz(4) flags(4) align(4)
                uint pType     = _reader.ReadUInt32();
                ulong pOffset  = _reader.ReadUInt32();
                ulong pVaddr   = _reader.ReadUInt32();
                ulong pPaddr   = _reader.ReadUInt32();
                ulong pFilesz  = _reader.ReadUInt32();
                ulong pMemsz   = _reader.ReadUInt32();
                uint pFlags    = _reader.ReadUInt32();     // flags is at end in ELF32
                ulong pAlign   = _reader.ReadUInt32();

                phHeaders[i] = new BinarySegment(pType, pFlags, pOffset, pVaddr, pPaddr, pFilesz, pMemsz, pAlign);
            }
            else
            {
                // ELF64 Phdr: type(4) flags(4) offset(8) vaddr(8) paddr(8) filesz(8) memsz(8) align(8)
                uint pType     = _reader.ReadUInt32();
                uint pFlags    = _reader.ReadUInt32();     // flags is early in ELF64
                ulong pOffset  = _reader.ReadUInt64();
                ulong pVaddr   = _reader.ReadUInt64();
                ulong pPaddr   = _reader.ReadUInt64();
                ulong pFilesz  = _reader.ReadUInt64();
                ulong pMemsz   = _reader.ReadUInt64();
                ulong pAlign   = _reader.ReadUInt64();

                phHeaders[i] = new BinarySegment(pType, pFlags, pOffset, pVaddr, pPaddr, pFilesz, pMemsz, pAlign);
            }
        }

        SectionHeaders = headers;
        ProgramHeaders = phHeaders;
    }

    // ====================================================================
    // STEP 3: Section Name Resolution
    // ====================================================================

    private void ResolveSectionNames()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    ElfParser.ResolveSectionNames()");
        if (_shStrIndex >= SectionHeaders.Length) return;

        var shstrtab = SectionHeaders[_shStrIndex];
        int shstrOffset = (int)shstrtab.FileOffset;

        for (int i = 0; i < SectionHeaders.Length; i++)
        {
            string name = _reader.ReadStringAt(shstrOffset + (int)SectionHeaders[i].NameOffset);
            SectionHeaders[i] = SectionHeaders[i] with { ResolvedName = name };
        }
    }

    // ====================================================================
    // STEP 4: Locate Key Sections
    // ====================================================================

    private void LocateKeySections()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    ElfParser.LocateKeySections()");
        foreach (var sh in SectionHeaders)
        {
            switch (sh.ResolvedName)
            {
                case ".data":    DataSection = sh; break;
                case ".rodata":  RoDataSection = sh; break;
                case ".bss":     BssSection = sh; break;
                case ".dynstr":  DynStrSection = sh; break;
                case ".dynsym":  DynSymSection = sh; break;
                case ".rela.plt": RelaPltSection = sh; break;
                case ".rel.plt":  RelPltSection = sh; break;
                case ".got":      GotSection = sh; break;
                case ".rela.dyn": RelaDynSection = sh; break;
                case ".rel.dyn":  RelDynSection = sh; break;
                case ".gcc_except_table": GccExceptTableSection = sh; break;
            }
        }
    }

    // ====================================================================
    // STEP 5: Dynamic Symbol Table
    //
    // ELF32 Sym = 16 bytes:
    //   st_name(4) st_value(4) st_size(4) st_info(1) st_other(1) st_shndx(2)
    //
    // ELF64 Sym = 24 bytes:
    //   st_name(4) st_info(1) st_other(1) st_shndx(2) st_value(8) st_size(8)
    //
    // NOTE: field ORDER is different between ELF32 and ELF64!
    // ====================================================================

    private void ParseDynSymbols()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    ElfParser.ParseDynSymbols()");
        if (DynSymSection is null || DynStrSection is null) return;

        var symSec = DynSymSection.Value;
        var strSec = DynStrSection.Value;
        int strBase = (int)strSec.FileOffset;

        int entrySize = Is32Bit ? 16 : 24;
        int symCount = symSec.EntrySize > 0 ? (int)(symSec.Size / symSec.EntrySize) : 0;
        if (symCount == 0) symCount = (int)(symSec.Size / (ulong)entrySize);
        var symbols = new BinarySymbol[symCount];

        for (int i = 0; i < symCount; i++)
        {
            _reader.Position = (int)(symSec.FileOffset + (ulong)(i * entrySize));

            uint nameOff;
            byte info, other;
            ushort shndx;
            ulong value, size;

            if (Is32Bit)
            {
                // ELF32: name(4) value(4) size(4) info(1) other(1) shndx(2)
                nameOff = _reader.ReadUInt32();
                value   = _reader.ReadUInt32();
                size    = _reader.ReadUInt32();
                info    = _reader.ReadByte();
                other   = _reader.ReadByte();
                shndx   = _reader.ReadUInt16();
            }
            else
            {
                // ELF64: name(4) info(1) other(1) shndx(2) value(8) size(8)
                nameOff = _reader.ReadUInt32();
                info    = _reader.ReadByte();
                other   = _reader.ReadByte();
                shndx   = _reader.ReadUInt16();
                value   = _reader.ReadUInt64();
                size    = _reader.ReadUInt64();
            }

            string name = _reader.ReadStringAt(strBase + (int)nameOff);

            symbols[i] = new BinarySymbol(nameOff, info, other, shndx, value, size)
            {
                ResolvedName = name
            };
        }

        DynSymbols = symbols;
    }

    // ====================================================================
    // STEP 6: PLT Relocations
    //
    // ELF32 uses .rel.plt (SHT_REL, 8 bytes: offset(4) + info(4))
    //   symIndex = info >> 8, type = info & 0xFF
    //
    // ELF64 uses .rela.plt (SHT_RELA, 24 bytes: offset(8) + info(8) + addend(8))
    //   symIndex = info >> 32, type = info & 0xFFFFFFFF
    // ====================================================================

    private void ParseRelaPlt()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    ElfParser.ParseRelaPlt()");
        if (DynSymbols == null || DynSymbols.Length == 0) return;

        if (Is32Bit)
        {
            // 32-bit: try .rel.plt first, then .rela.plt
            var section = RelPltSection ?? RelaPltSection;
            if (section is null) return;

            var sec = section.Value;
            bool hasAddend = sec.ResolvedName == ".rela.plt";
            int entSize = hasAddend ? 12 : 8; // Elf32_Rela=12, Elf32_Rel=8
            int count = sec.EntrySize > 0 ? (int)(sec.Size / sec.EntrySize) : (int)(sec.Size / (ulong)entSize);

            for (int i = 0; i < count; i++)
            {
                _reader.Position = (int)(sec.FileOffset + (ulong)(i * entSize));

                ulong r_offset = _reader.ReadUInt32();
                uint r_info = _reader.ReadUInt32();
                // int r_addend = hasAddend ? _reader.ReadInt32() : 0; // not needed for PLT mapping

                // ELF32: symIndex = r_info >> 8, type = r_info & 0xFF
                uint symIndex = r_info >> 8;

                if (symIndex > 0 && symIndex < DynSymbols.Length)
                {
                    string symName = DynSymbols[symIndex].ResolvedName ?? "";
                    if (!string.IsNullOrEmpty(symName))
                    {
                        PltGotSymbols[r_offset] = symName;
                    }
                }
            }
        }
        else
        {
            // 64-bit: .rela.plt
            if (RelaPltSection is null) return;

            var relaSec = RelaPltSection.Value;
            int relaCount = relaSec.EntrySize > 0 ? (int)(relaSec.Size / relaSec.EntrySize) : 0;
            if (relaCount == 0) relaCount = (int)(relaSec.Size / 24); // Standard Elf64_Rela is 24 bytes

            for (int i = 0; i < relaCount; i++)
            {
                _reader.Position = (int)(relaSec.FileOffset + (ulong)(i * 24));

                ulong r_offset = _reader.ReadUInt64(); // The GOT address
                ulong r_info   = _reader.ReadUInt64();
                long  r_addend = _reader.ReadInt64();

                // r_info: top 32 bits = symbol index, bottom 32 bits = relocation type
                uint symIndex = (uint)(r_info >> 32);
                uint type     = (uint)(r_info & 0xFFFFFFFF);

                if (symIndex > 0 && symIndex < DynSymbols.Length)
                {
                    string symName = DynSymbols[symIndex].ResolvedName ?? "";
                    if (!string.IsNullOrEmpty(symName))
                    {
                        PltGotSymbols[r_offset] = symName;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Convert a virtual address to a file offset using program headers.
    /// </summary>
    public long VirtualToFileOffset(ulong virtualAddr)
    {
        foreach (var ph in ProgramHeaders)
        {
            if (ph.Type != 1) continue; // PT_LOAD
            if (virtualAddr >= ph.VirtualAddr && virtualAddr < ph.VirtualAddr + ph.MemSize)
            {
                // Calculate the offset within this segment
                ulong offsetInSegment = virtualAddr - ph.VirtualAddr;

                // Validate against FileSize, not just MemSize.
                // For .bss segments (uninitialized data), MemSize >> FileSize.
                // Addresses beyond FileSize are architecturally zero-filled at runtime
                // but have no backing data in the file — return -1 to signal this.
                if (offsetInSegment >= ph.FileSize)
                    return -1;

                return (long)(ph.FileOffset + offsetInSegment);
            }
        }
        return -1;
    }

    /// <summary>
    /// Search for a dynamic symbol by name.
    /// </summary>
    public BinarySymbol? FindSymbol(string name)
    {
        foreach (var sym in DynSymbols)
        {
            if (sym.ResolvedName == name) return sym;
        }
        return null;
    }

    /// <summary>
    /// Search for a section by name.
    /// </summary>
    public BinarySectionHeader? FindSectionByName(string name)
    {
        foreach (var sh in SectionHeaders)
        {
            if (sh.ResolvedName == name) return sh;
        }
        return null;
    }

    // ====================================================================
    // STEP 7: PLT Stub Resolution (ARM64 only)
    //
    // Each PLT entry on AArch64 is 16 bytes:
    //   ADRP Xn, <page>         ; load high bits of GOT slot address
    //   LDR  Xn, [Xn, #offset]  ; load function pointer from GOT
    //   ADD  Xn, Xn, #0         ; (optional, sometimes NOP)
    //   BR   Xn                  ; jump to function
    //
    // By decoding ADRP+LDR we derive the GOT slot VA, which maps to
    // a symbol name via PltGotSymbols (from .rela.plt relocations).
    //
    // Skipped for 32-bit binaries (only needed for ARM64 decompilation).
    // ====================================================================

    private void ParsePltStubs()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    ElfParser.ParsePltStubs()");
        var pltSection = FindSectionByName(".plt");
        if (pltSection is null || PltGotSymbols.Count == 0) return;

        var plt = pltSection.Value;
        long pltFileOffset = (long)plt.FileOffset;
        int pltSize = (int)plt.Size;
        ulong pltVA = plt.VirtualAddr;

        if (pltFileOffset < 0 || pltFileOffset + pltSize > _reader.Length) return;

        // AArch64 PLT entries are 16 bytes each
        // First entry (PLT0) is a special resolver stub — skip it
        for (int off = 16; off + 16 <= pltSize; off += 16)
        {
            ulong entryVA = pltVA + (ulong)off;
            int filePos = (int)pltFileOffset + off;
            if (filePos + 8 > _reader.Length) break;

            // Decode ADRP (instruction 0)
            _reader.Position = filePos;
            uint raw0 = _reader.ReadUInt32();
            uint raw1 = _reader.ReadUInt32();

            // ADRP: bits [31]=1, [28:24]=10000
            if ((raw0 & 0x9F000000) != 0x90000000) continue;

            // Extract ADRP immediate
            uint immLo = (raw0 >> 29) & 0x3;
            uint immHi = (raw0 >> 5) & 0x7FFFF;
            long pageOff = (long)((immHi << 2) | immLo) << 12;
            // Sign extend from 33 bits (bit 32 is the sign bit of the 33-bit result)
            // The mask must set bits 33..63 to 1 for negative offsets.
            if ((pageOff & (1L << 32)) != 0)
                pageOff |= unchecked((long)0xFFFFFFFE00000000L);
            ulong page = (entryVA & ~0xFFFUL) + (ulong)pageOff;

            // LDR Xn, [Xn, #imm12]: bits [31:22] = 1111100101
            if ((raw1 & 0xFFC00000) != 0xF9400000) continue;

            uint ldrImm12 = ((raw1 >> 10) & 0xFFF) << 3; // scale by 8 for 64-bit LDR
            ulong gotSlotVA = page + ldrImm12;

            // Look up the GOT slot in our rela.plt-derived map
            if (PltGotSymbols.TryGetValue(gotSlotVA, out string? symName))
            {
                PltStubSymbols[entryVA] = symName;
            }
        }
    }

    // ====================================================================
    // STEP 8b: ARM32 PLT Stub Resolution
    //
    // ARM32 PLT layout (ARM-mode instructions, 4 bytes each):
    //   PLT0: 32 bytes (resolver stub, skipped)
    //   PLTn: 16 bytes each (3 instructions + 4 bytes padding)
    //     ADD IP, PC, #imm0     ; encoded with ARM rotated immediate
    //     ADD IP, IP, #imm1     ; encoded with ARM rotated immediate
    //     LDR PC, [IP, #±imm2]! ; U-bit selects add/sub, imm12 field
    //
    // GOT address = (stub_VA + 8) + imm0 + imm1 + imm2
    //   where +8 accounts for the ARM pipeline (PC = current + 8)
    //
    // The GOT address is mapped to a symbol name via PltGotSymbols
    // (built from .rel.plt relocations in ParseRelaPlt).
    // ====================================================================

    private void ParsePltStubs32()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    ElfParser.ParsePltStubs32()");
        var pltSection = FindSectionByName(".plt");
        if (pltSection is null || PltGotSymbols.Count == 0) return;

        var plt = pltSection.Value;
        long pltFileOffset = (long)plt.FileOffset;
        int pltSize = (int)plt.Size;
        ulong pltVA = plt.VirtualAddr;

        if (pltFileOffset < 0 || pltFileOffset + pltSize > _reader.Length) return;

        // ARM32 PLT0 is 32 bytes, each subsequent entry is 16 bytes
        int nEntries = (pltSize - 32) / 16;

        for (int j = 0; j < nEntries; j++)
        {
            int off = 32 + j * 16;
            ulong entryVA = pltVA + (ulong)off;
            int filePos = (int)pltFileOffset + off;
            if (filePos + 12 > _reader.Length) break;

            _reader.Position = filePos;
            uint raw0 = _reader.ReadUInt32();
            uint raw1 = _reader.ReadUInt32();
            uint raw2 = _reader.ReadUInt32();

            // Decode ARM rotated immediates for ADD instructions
            uint v0 = DecodeArmRotatedImm(raw0);
            uint v1 = DecodeArmRotatedImm(raw1);

            // LDR PC, [IP, #±imm12]!
            // Bit 23 (U-bit): 1 = add offset, 0 = subtract offset
            bool addOffset = ((raw2 >> 23) & 1) == 1;
            int ldrImm = (int)(raw2 & 0xFFF);
            if (!addOffset) ldrImm = -ldrImm;

            // GOT address = PC(stub+8) + imm0 + imm1 + ldr_offset
            ulong pcVal = entryVA + 8;
            ulong gotAddr = (pcVal + v0 + v1 + (ulong)(uint)(ldrImm)) & 0xFFFFFFFF;

            if (PltGotSymbols.TryGetValue(gotAddr, out string? symName))
            {
                PltStubSymbols[entryVA] = symName;
            }
        }
    }

    /// <summary>
    /// Decode an ARM "rotated immediate" encoding.
    /// Format: bits [11:8] = rotate amount / 2, bits [7:0] = 8-bit immediate.
    /// Result = ROR(imm8, rotate*2).
    /// Source: ARM Architecture Reference Manual, §A5.2.4.
    /// </summary>
    private static uint DecodeArmRotatedImm(uint raw)
    {
        int rotate = (int)((raw >> 8) & 0xF);
        uint imm8 = raw & 0xFF;
        if (rotate == 0) return imm8;
        int shift = rotate * 2;
        return (imm8 >> shift) | (imm8 << (32 - shift));
    }
}
