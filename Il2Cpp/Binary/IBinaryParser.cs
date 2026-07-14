using System;
using System.Collections.Generic;

namespace Rosetta.Binary;

/// <summary>
/// Universal interface for parsing native binaries (ELF for Android/Linux, PE for Windows).
/// </summary>
public interface IBinaryParser
{
    bool Is32Bit { get; }
    int PointerSize { get; }
    ushort Machine { get; }
    string ArchitectureName { get; }
    bool IsX64 { get; }
    ulong EntryPoint { get; }

    BinarySectionHeader[] SectionHeaders { get; }
    BinarySegment[] ProgramHeaders { get; }
    BinarySymbol[] DynSymbols { get; }

    BinarySectionHeader? DataSection { get; }
    BinarySectionHeader? RoDataSection { get; }
    BinarySectionHeader? BssSection { get; }
    BinarySectionHeader? DynStrSection { get; }
    BinarySectionHeader? DynSymSection { get; }
    BinarySectionHeader? RelaPltSection { get; }
    BinarySectionHeader? RelPltSection { get; }
    BinarySectionHeader? GotSection { get; }
    BinarySectionHeader? RelaDynSection { get; }
    BinarySectionHeader? RelDynSection { get; }
    BinarySectionHeader? GccExceptTableSection { get; }

    Dictionary<ulong, string> PltGotSymbols { get; }
    Dictionary<ulong, string> PltStubSymbols { get; }
    Dictionary<ulong, ulong> RelocationMap { get; }

    void Parse();
    long VirtualToFileOffset(ulong virtualAddr);
    BinarySymbol? FindSymbol(string name);
    BinarySectionHeader? FindSectionByName(string name);
}
