// Source of Truth: Transpiler Omnibus §18.1, Native Toolchain Omnibus §21
// CRITICAL: Verified against decompiled WriteGlobalMetadataDat.cs

namespace Rosetta.Common;

/// <summary>
/// Compile-time constants derived directly from the IL2CPP source headers.
/// </summary>
public static class Constants
{
    /// <summary>Magic number at offset 0x00 of global-metadata.dat. Little-endian: AF 1B B1 FA.</summary>
    public const uint MetadataMagic = 0xFAB11BAF;

    /// <summary>Supported metadata versions.</summary>
    public static readonly int[] SupportedVersions = [23, 24, 27, 29, 31, 35, 38, 39, 104, 105, 106];

    /// <summary>Minimum supported metadata version.</summary>
    public const int MinMetadataVersion = 23;

    /// <summary>Maximum supported metadata version.</summary>
    public const int MaxMetadataVersion = 106;

    /// <summary>
    /// Number of PHYSICAL sections in the section table (v39).
    /// v39: 31 sections × 12 bytes = 372 byte table.
    /// v24-v29: variable count of offset+size pairs × 8 bytes.
    /// </summary>
    public const int MetadataPhysicalSectionCount_V39 = 31;

    /// <summary>Size of the file header: Magic(4) + Version(4).</summary>
    public const int MetadataHeaderBaseSize = 8;

    /// <summary>Size of each section header entry in v39: Offset(4) + Size(4) + ItemCount(4).</summary>
    public const int SectionHeaderEntrySize_V39 = 12;

    /// <summary>Size of each section header entry in v24-v29: Offset(4) + Size(4).</summary>
    public const int SectionHeaderEntrySize_Legacy = 8;

    /// <summary>ELF magic: 0x7F 'E' 'L' 'F'.</summary>
    public const uint ElfMagic = 0x464C457F;

    /// <summary>VTable slot sentinel from il2cpp-config.h: kInvalidIl2CppMethodSlot = 0xFFFF.</summary>
    public const ushort InvalidMethodSlot = 0xFFFF;

    /// <summary>Section alignment in global-metadata.dat (4 bytes).</summary>
    public const int SectionAlignment = 4;

    // ─── IL2CPP Managed Object Layout (ARM64, 64-bit) ───────────────────────

    /// <summary>
    /// Il2CppObject header: klass (8 bytes) + monitor (8 bytes) = 16 bytes.
    /// All managed objects start with this header.
    /// </summary>
    public const int ObjectHeaderSize = 0x10;

    /// <summary>Offset to Il2CppArray.bounds pointer: [arr + 0x10].
    /// For multi-dimensional arrays, this is a non-null pointer to Il2CppArrayBounds[].
    /// For 1D arrays, this is null.</summary>
    public const int ArrayBoundsOffset = 0x10;

    /// <summary>Offset to Il2CppArray.max_length: [arr + 0x18].
    /// Total element count for 1D arrays (int32/uintptr_t).</summary>
    public const int ArrayLengthOffset = 0x18;

    /// <summary>Offset to first array element: [arr + 0x20].
    /// Elements are stored inline starting at this offset.</summary>
    public const int ArrayDataOffset = 0x20;

    /// <summary>Size of each Il2CppArrayBounds struct (ARM64):
    /// il2cpp_array_size_t length (8 bytes) + int32_t lower_bound (4 bytes) + padding (4 bytes) = 16 bytes.
    /// bounds[0] at +0x00, bounds[1] at +0x10, bounds[2] at +0x20, etc.</summary>
    public const int ArrayBoundsStructSize = 0x10;

    /// <summary>Offset to String.m_stringLength: [str + 0x10].
    /// Note: This is the SAME offset as ArrayBoundsOffset — context determines meaning.</summary>
    public const int StringLengthOffset = 0x10;

    /// <summary>Offset to the class initialization flag in Il2CppClass: [typeof(T) + 0xE4].
    /// When true, the class's static constructor has run.
    /// This is dynamically computed by RuntimeFunctionProber at runtime.</summary>
    public static int ClassInitFlagOffset = 0xE4;
}
