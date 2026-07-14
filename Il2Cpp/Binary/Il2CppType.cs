using Rosetta.Common;

namespace Rosetta.Binary;

/// <summary>
/// Parsed Il2CppType struct from the native binary.
///
/// Source Path: il2cpp-runtime-metadata.h lines 54-76
///
/// Original C struct:
///   typedef struct Il2CppType {
///     union {
///       void* dummy;
///       TypeDefinitionIndex __klassIndex;       // for VALUETYPE and CLASS
///       const Il2CppType *type;                 // for PTR and SZARRAY
///       Il2CppArrayType *array;                 // for ARRAY
///       GenericParameterIndex __genericParameterIndex; // for VAR and MVAR
///       Il2CppGenericClass *generic_class;       // for GENERICINST
///     } data;                                   // +0, pointer-sized (4 on 32-bit, 8 on 64-bit)
///     unsigned int attrs    : 16;               // +ptrSize, bits 0-15
///     Il2CppTypeEnum type   : 8;                // +ptrSize, bits 16-23
///     unsigned int num_mods : 5;                // +ptrSize, bits 24-28
///     unsigned int byref    : 1;                // +ptrSize, bit 29
///     unsigned int pinned   : 1;                // +ptrSize, bit 30
///     unsigned int valuetype: 1;                // +ptrSize, bit 31
///   } Il2CppType;
///   // Total: 8 bytes on 32-bit (data=4 + bitfield=4)
///   // Total: 12 bytes on 64-bit (data=8 + bitfield=4)
///
/// Mapping Logic:
///   - data is read as pointer-sized (uint32 on 32-bit, uint64 on 64-bit)
///   - bitfield is read as uint32 and decomposed via shifts/masks
///   - For CLASS/VALUETYPE: data = TypeDefinitionIndex (int32 in low bits)
///   - For VAR/MVAR: data = GenericParameterIndex
///   - For PTR/SZARRAY: data = pointer to another Il2CppType
///   - For GENERICINST: data = pointer to Il2CppGenericClass
/// </summary>
public readonly struct Il2CppType
{
    /// <summary>Size in bytes on 64-bit ARM64.</summary>
    public const int SizeOf64 = 12;

    /// <summary>Size in bytes on 32-bit ARM32.</summary>
    public const int SizeOf32 = 8;

    /// <summary>Size in bytes on ARM64 (legacy compatibility).</summary>
    public const int SizeOf = 12;

    /// <summary>Get size for given pointer width.</summary>
    public static int GetSizeOf(bool is32Bit) => is32Bit ? SizeOf32 : SizeOf64;

    /// <summary>The data union value (pointer-sized: 4 or 8 bytes).</summary>
    public ulong Data { get; init; }

    /// <summary>Param attributes or field flags (bits 0-15).</summary>
    public ushort Attrs { get; init; }

    /// <summary>The type tag from Il2CppTypeEnum (bits 16-23).</summary>
    public Il2CppTypeEnum TypeEnum { get; init; }

    /// <summary>Number of custom modifiers (bits 24-28, max 31).</summary>
    public byte NumMods { get; init; }

    /// <summary>Is this a by-reference type? (bit 29).</summary>
    public bool ByRef { get; init; }

    /// <summary>Is this pinned? (bit 30).</summary>
    public bool Pinned { get; init; }

    /// <summary>Is this a value type? (bit 31).</summary>
    public bool ValueType { get; init; }

    /// <summary>
    /// Parse an Il2CppType from raw bytes (64-bit: 12 bytes, 32-bit: 8 bytes).
    ///
    /// Source: il2cpp-runtime-metadata.h lines 54-76
    ///   data union: bytes [0..ptrSize-1]
    ///   bitfield:   bytes [ptrSize..ptrSize+3]
    ///     attrs(16) | type(8) | num_mods(5) | byref(1) | pinned(1) | valuetype(1)
    /// </summary>
    public static Il2CppType Parse(ReadOnlySpan<byte> bytes, bool is32Bit = false)
    {
        ulong data;
        uint bitfield;

        if (is32Bit)
        {
            data = BitConverter.ToUInt32(bytes[..4]);
            bitfield = BitConverter.ToUInt32(bytes[4..8]);
        }
        else
        {
            data = BitConverter.ToUInt64(bytes[..8]);
            bitfield = BitConverter.ToUInt32(bytes[8..12]);
        }

        return new Il2CppType
        {
            Data       = data,
            Attrs      = (ushort)(bitfield & 0xFFFF),
            TypeEnum   = (Il2CppTypeEnum)((bitfield >> 16) & 0xFF),
            NumMods    = (byte)((bitfield >> 24) & 0x1F),
            ByRef      = ((bitfield >> 29) & 1) != 0,
            Pinned     = ((bitfield >> 30) & 1) != 0,
            ValueType  = ((bitfield >> 31) & 1) != 0,
        };
    }

    /// <summary>
    /// For CLASS/VALUETYPE: the TypeDefinitionIndex.
    /// Source: il2cpp-runtime-metadata.h line 60:
    ///   TypeDefinitionIndex __klassIndex;
    /// </summary>
    public int KlassIndex => (int)Data;

    /// <summary>
    /// For VAR/MVAR: the GenericParameterIndex.
    /// Source: il2cpp-runtime-metadata.h line 65:
    ///   GenericParameterIndex __genericParameterIndex;
    /// </summary>
    public int GenericParameterIndex => (int)Data;

    /// <summary>
    /// For PTR/SZARRAY: the pointer to the element Il2CppType.
    /// For GENERICINST: the pointer to the Il2CppGenericClass.
    /// Source: il2cpp-runtime-metadata.h lines 62-67
    /// </summary>
    public ulong DataPointer => Data;
}
