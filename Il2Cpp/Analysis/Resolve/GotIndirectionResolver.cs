using Rosetta.Binary;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.Resolve;

/// <summary>
/// Follows Global Offset Table (GOT) indirection to resolve the ultimate
/// target address of ADRP+LDR pairs that load through the GOT.
///
/// Source Evidence Chain:
///   1. Position-Independent Code (PIC) on AArch64 uses ADRP+LDR to load a
///      pointer from the GOT, which is then used to access the actual global.
///   2. GOT entries are filled by the dynamic linker using R_AARCH64_RELATIVE
///      relocations from the .rela.dyn section (type 0x403).
///   3. The relocation addend gives the actual runtime VA of the target.
///
/// Example (from binary):
///   ADRP X8, #GOT_page       ; X8 = page containing GOT
///   LDR  X8, [X8, #GOT_off]  ; X8 = GOT[VA] → relocated to target in .bss/.data
///   LDR  X9, [X8, #field]    ; access field at the actual target
///
///   GOT[0x2413920] has reloc R_AARCH64_RELATIVE with addend 0x25647F8 (.bss)
///   → The ADRP+LDR resolves to the .bss variable, not the GOT slot.
/// </summary>
public sealed class GotIndirectionResolver
{
    private readonly Dictionary<ulong, ulong> _gotToTarget = new();

    /// <summary>Total GOT entries resolved via .rela.dyn relocations.</summary>
    public int ResolvedCount => _gotToTarget.Count;

    /// <summary>
    /// Build the GOT→target map by scanning .rela.dyn relocations for entries
    /// whose r_offset falls within the .got section.
    ///
    /// Source: ELF AArch64 ABI
    ///   R_AARCH64_RELATIVE (0x403): runtime value = base + addend
    ///   For PIE/PIC, the base is the load address, but since we're analyzing
    ///   the ELF statically, the addend IS the virtual address of the target.
    /// </summary>
    public void Build(IBinaryParser elf, byte[] binary)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"GotIndirectionResolver.Build()");
        var gotSection = elf.GotSection;

        if (gotSection == null)
            return;

        ulong gotStart = gotSection.Value.VirtualAddr;
        ulong gotEnd = gotStart + gotSection.Value.Size;

        if (elf.Is32Bit)
        {
            // ELF32: .rel.dyn entries (8 bytes each: r_offset u32, r_info u32, NO addend)
            // R_ARM_RELATIVE = 0x17
            var relDynSection = elf.RelDynSection;
            if (relDynSection == null) return;

            long relFileOffset = (long)relDynSection.Value.FileOffset;
            long relSize = (long)relDynSection.Value.Size;
            int entryCount = (int)(relSize / 8);

            for (int i = 0; i < entryCount; i++)
            {
                long entryOff = relFileOffset + i * 8;
                if (entryOff + 8 > binary.Length)
                    break;

                uint rOffset = BitConverter.ToUInt32(binary, (int)entryOff);
                uint rInfo = BitConverter.ToUInt32(binary, (int)entryOff + 4);

                uint rType = rInfo & 0xFF; // ELF32: type is low 8 bits of r_info

                // R_ARM_RELATIVE = 0x17
                if (rType != 0x17)
                    continue;

                if (rOffset < gotStart || rOffset >= gotEnd)
                    continue;

                // For .rel.dyn (no explicit addend), the addend is stored
                // at the relocation target address (the GOT slot itself).
                long slotFileOffset = elf.VirtualToFileOffset(rOffset);
                if (slotFileOffset < 0 || slotFileOffset + 4 > binary.Length)
                    continue;

                uint target = BitConverter.ToUInt32(binary, (int)slotFileOffset);
                if (target > 0)
                {
                    _gotToTarget[rOffset] = target;
                }
            }
        }
        else
        {
            // ELF64: .rela.dyn entries (24 bytes each: r_offset u64, r_info u64, r_addend i64)
            // R_AARCH64_RELATIVE = 0x403
            var relaDynSection = elf.RelaDynSection;
            if (relaDynSection == null) return;

            long relaFileOffset = (long)relaDynSection.Value.FileOffset;
            long relaSize = (long)relaDynSection.Value.Size;
            int entryCount = (int)(relaSize / 24);

            for (int i = 0; i < entryCount; i++)
            {
                long entryOff = relaFileOffset + i * 24;
                if (entryOff + 24 > binary.Length)
                    break;

                ulong rOffset = BitConverter.ToUInt64(binary, (int)entryOff);
                ulong rInfo = BitConverter.ToUInt64(binary, (int)entryOff + 8);
                long rAddend = BitConverter.ToInt64(binary, (int)entryOff + 16);

                uint rType = (uint)(rInfo & 0xFFFFFFFF);

                // Only process R_AARCH64_RELATIVE relocations targeting .got
                if (rType != 0x403)
                    continue;

                if (rOffset < gotStart || rOffset >= gotEnd)
                    continue;

                if (rAddend > 0)
                {
                    _gotToTarget[rOffset] = (ulong)rAddend;
                }
            }
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  GOT resolver: {_gotToTarget.Count} entries mapped");
    }

    /// <summary>
    /// Check if a virtual address is within the .got section.
    /// </summary>
    public bool IsGotAddress(ulong va, IBinaryParser elf)
    {
        var got = elf.GotSection;
        if (got == null) return false;
        return va >= got.Value.VirtualAddr && va < got.Value.VirtualAddr + got.Value.Size;
    }

    /// <summary>
    /// Follow GOT indirection: if <paramref name="gotSlotVA"/> is a GOT entry,
    /// return the actual target VA from the relocation. Returns null if not found.
    /// </summary>
    public ulong? FollowIndirection(ulong gotSlotVA)
    {
        return _gotToTarget.TryGetValue(gotSlotVA, out ulong target) ? target : null;
    }
}
