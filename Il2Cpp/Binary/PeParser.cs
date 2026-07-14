using System;
using System.Collections.Generic;
using System.Text;
using Rosetta.Common;
using Rosetta.IO;
using Rosetta.Pipeline;

namespace Rosetta.Binary;

/// <summary>
/// Parser for Windows Portable Executable (PE) binaries.
/// Reads GameAssembly.dll to extract sections, segments, and relocations.
/// </summary>
public sealed class PeParser : IBinaryParser
{
    private readonly EndianBinaryReader _reader;

    public bool Is32Bit { get; private set; }
    public int PointerSize => Is32Bit ? 4 : 8;
    public ushort Machine { get; private set; }
    
    public string ArchitectureName => Machine switch
    {
        0x014C => "x86",
        0x8664 => "x86_64",
        0x01C0 => "ARM",
        0xAA64 => "AArch64",
        _ => $"Unknown (0x{Machine:X4})"
    };
    
    public bool IsX64 => Machine == 0x8664;
    
    public ulong EntryPoint { get; private set; }

    public BinarySectionHeader[] SectionHeaders { get; private set; } = [];
    public BinarySegment[] ProgramHeaders { get; private set; } = [];
    public BinarySymbol[] DynSymbols { get; private set; } = [];

    // Common PE sections mapped to ELF equivalents
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
    public Dictionary<ulong, string> PltStubSymbols { get; private set; } = new();
    public Dictionary<ulong, ulong> RelocationMap { get; private set; } = new();

    private ulong _imageBase;

    public PeParser(byte[] binaryData)
    {
        _reader = new EndianBinaryReader(binaryData);
    }

    public void Parse()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace("PeParser.Parse()");

        // 1. DOS Header
        _reader.Position = 0;
        ushort magic = _reader.ReadUInt16();
        if (magic != 0x5A4D) // "MZ"
        {
            throw new InvalidOperationException($"Invalid DOS magic: 0x{magic:X4}");
        }

        _reader.Position = 0x3C;
        uint peOffset = _reader.ReadUInt32();

        // 2. PE Signature
        _reader.Position = (int)peOffset;
        uint peMagic = _reader.ReadUInt32();
        if (peMagic != 0x00004550) // "PE\0\0"
        {
            throw new InvalidOperationException($"Invalid PE magic: 0x{peMagic:X8}");
        }

        // 3. COFF File Header
        Machine = _reader.ReadUInt16();
        ushort numberOfSections = _reader.ReadUInt16();
        _reader.ReadUInt32(); // TimeDateStamp
        _reader.ReadUInt32(); // PointerToSymbolTable
        _reader.ReadUInt32(); // NumberOfSymbols
        ushort sizeOfOptionalHeader = _reader.ReadUInt16();
        ushort characteristics = _reader.ReadUInt16();

        // 4. Optional Header
        int optionalHeaderOffset = _reader.Position;
        ushort optionalHeaderMagic = _reader.ReadUInt16();
        
        Is32Bit = optionalHeaderMagic == 0x10B; // PE32
        bool is64Bit = optionalHeaderMagic == 0x20B; // PE32+

        if (!Is32Bit && !is64Bit)
        {
            throw new InvalidOperationException($"Unknown Optional Header magic: 0x{optionalHeaderMagic:X4}");
        }

        _reader.Position = optionalHeaderOffset + (Is32Bit ? 28 : 24);
        _imageBase = Is32Bit ? _reader.ReadUInt32() : _reader.ReadUInt64();

        // Read NumberOfRvaAndSizes
        _reader.Position = optionalHeaderOffset + (Is32Bit ? 92 : 108);
        uint numberOfRvaAndSizes = _reader.ReadUInt32();

        uint relocRva = 0;
        uint relocSize = 0;

        // Base Relocation Directory is at index 5
        if (numberOfRvaAndSizes > 5)
        {
            _reader.Position = optionalHeaderOffset + (Is32Bit ? 96 : 112) + (5 * 8);
            relocRva = _reader.ReadUInt32();
            relocSize = _reader.ReadUInt32();
        }

        // 5. Section Headers
        _reader.Position = (int)(optionalHeaderOffset + sizeOfOptionalHeader);
        SectionHeaders = new BinarySectionHeader[numberOfSections];
        
        for (int i = 0; i < numberOfSections; i++)
        {
            ReadOnlySpan<byte> nameBytes = _reader.ReadBytes(8);
            string name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
            
            uint virtualSize = _reader.ReadUInt32();
            uint virtualAddress = _reader.ReadUInt32();
            uint sizeOfRawData = _reader.ReadUInt32();
            uint pointerToRawData = _reader.ReadUInt32();
            _reader.ReadUInt32(); // PointerToRelocations
            _reader.ReadUInt32(); // PointerToLinenumbers
            _reader.ReadUInt16(); // NumberOfRelocations
            _reader.ReadUInt16(); // NumberOfLinenumbers
            uint sectionCharacteristics = _reader.ReadUInt32();

            SectionHeaders[i] = new BinarySectionHeader(
                NameOffset: 0, 
                Type: 0, 
                Flags: sectionCharacteristics,
                VirtualAddr: virtualAddress + _imageBase, 
                FileOffset: pointerToRawData,
                Size: Math.Max(virtualSize, sizeOfRawData),
                Link: 0,
                Info: 0,
                AddrAlign: 0,
                EntrySize: 0
            ) { ResolvedName = name };

            if (name == ".data") DataSection = SectionHeaders[i];
            else if (name == ".rdata" || name == ".rodata") RoDataSection = SectionHeaders[i];
            else if (name == ".bss") BssSection = SectionHeaders[i];
        }

        // 6. Parse Base Relocations
        if (relocRva != 0 && relocSize != 0)
        {
            ParseBaseRelocations(relocRva, relocSize);
        }
        else
        {
            // If no Base Relocations, we must scan the raw sections because RegistrationResolver expects a RelocationMap.
            // (Windows DLLs should always have Base Relocations, so this is rare).
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace("PeParser: No Base Relocations found.");
        }
    }

    private void ParseBaseRelocations(uint relocRva, uint relocSize)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"PeParser.ParseBaseRelocations(rva=0x{relocRva:X}, size=0x{relocSize:X})");
        
        long fileOffset = VirtualToFileOffset(relocRva + _imageBase);
        if (fileOffset <= 0) return;

        int currentOffset = (int)fileOffset;
        int endOffset = currentOffset + (int)relocSize;

        while (currentOffset < endOffset)
        {
            _reader.Position = currentOffset;
            uint pageRva = _reader.ReadUInt32();
            uint blockSize = _reader.ReadUInt32();
            
            if (blockSize == 0) break;

            int entryCount = (int)(blockSize - 8) / 2;
            currentOffset += (int)blockSize;

            for (int i = 0; i < entryCount; i++)
            {
                ushort entry = _reader.ReadUInt16();
                int type = entry >> 12;
                int offset = entry & 0xFFF;

                // IMAGE_REL_BASED_HIGHLOW (3) for PE32
                // IMAGE_REL_BASED_DIR64 (10) for PE32+
                if (type == 3 || type == 10)
                {
                    ulong pointerRva = pageRva + (uint)offset;
                    ulong pointerVa = _imageBase + pointerRva;

                    long targetFileOffset = VirtualToFileOffset(pointerVa);
                    if (targetFileOffset > 0 && targetFileOffset + PointerSize <= _reader.Length)
                    {
                        int oldPos = _reader.Position;
                        _reader.Position = (int)targetFileOffset;
                        ulong targetPointer = Is32Bit ? _reader.ReadUInt32() : _reader.ReadUInt64();
                        _reader.Position = oldPos;

                        if (targetPointer > 0)
                        {
                            RelocationMap[pointerVa] = targetPointer;
                        }
                    }
                }
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"  PeParser: Added {RelocationMap.Count} relocations from Base Relocation Table.");
    }

    public long VirtualToFileOffset(ulong virtualAddr)
    {
        foreach (var sec in SectionHeaders)
        {
            if (virtualAddr >= sec.VirtualAddr && virtualAddr < sec.VirtualAddr + sec.Size)
            {
                return (long)(sec.FileOffset + (virtualAddr - sec.VirtualAddr));
            }
        }
        return -1;
    }

    public BinarySymbol? FindSymbol(string name)
    {
        foreach (var sym in DynSymbols)
        {
            if (sym.ResolvedName == name) return sym;
        }
        return null;
    }

    public BinarySectionHeader? FindSectionByName(string name)
    {
        foreach (var sh in SectionHeaders)
        {
            if (sh.ResolvedName == name) return sh;
        }
        return null;
    }
}
