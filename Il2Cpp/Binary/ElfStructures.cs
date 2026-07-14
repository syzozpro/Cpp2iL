namespace Rosetta.Binary;

/// <summary>
/// Parsed binary section header.
/// Source: ELF/PE specification.
/// </summary>
public readonly record struct BinarySectionHeader(
    uint NameOffset,    // sh_name — offset into .shstrtab
    uint Type,          // sh_type
    ulong Flags,        // sh_flags
    ulong VirtualAddr,  // sh_addr
    ulong FileOffset,   // sh_offset
    ulong Size,         // sh_size
    uint Link,          // sh_link
    uint Info,          // sh_info
    ulong AddrAlign,    // sh_addralign
    ulong EntrySize     // sh_entsize
)
{
    public string? ResolvedName { get; init; }
}

/// <summary>
/// Parsed binary program header (segment).
/// </summary>
public readonly record struct BinarySegment(
    uint Type,          // p_type
    uint Flags,         // p_flags
    ulong FileOffset,   // p_offset
    ulong VirtualAddr,  // p_vaddr
    ulong PhysAddr,     // p_paddr
    ulong FileSize,     // p_filesz
    ulong MemSize,      // p_memsz
    ulong Align         // p_align
);

/// <summary>
/// Parsed binary symbol entry.
/// </summary>
public readonly record struct BinarySymbol(
    uint NameOffset,    // st_name
    byte Info,          // st_info
    byte Other,         // st_other
    ushort SectionIndex,// st_shndx
    ulong Value,        // st_value
    ulong Size          // st_size
)
{
    public string? ResolvedName { get; init; }
    public byte Bind => (byte)(Info >> 4);
    public byte Type => (byte)(Info & 0xF);
}
