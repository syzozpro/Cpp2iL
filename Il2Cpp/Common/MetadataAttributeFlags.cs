// Source of Truth: il2cpp-tabledefs.h — Native Toolchain Omnibus §26.3

namespace Rosetta.Common;

/// <summary>Method attribute bitmasks from ECMA-335 §II.23.1.10.</summary>
[Flags]
public enum MethodAttributes : ushort
{
    MemberAccessMask       = 0x0007,
    CompilerControlled     = 0x0000,
    Private                = 0x0001,
    FamANDAssem            = 0x0002,
    Assembly               = 0x0003,
    Family                 = 0x0004,
    FamORAssem             = 0x0005,
    Public                 = 0x0006,
    Static                 = 0x0010,
    Final                  = 0x0020,
    Virtual                = 0x0040,
    HideBySig              = 0x0080,
    VtableLayoutMask       = 0x0100,
    ReuseSlot              = 0x0000,
    NewSlot                = 0x0100,
    Strict                 = 0x0200,
    Abstract               = 0x0400,
    SpecialName            = 0x0800,
    PInvokeImpl            = 0x2000,
    UnmanagedExport        = 0x0008,
    RTSpecialName          = 0x1000,
    HasSecurity            = 0x4000,
    RequireSecObject       = 0x8000,
}

/// <summary>Type attribute bitmasks from ECMA-335 §II.23.1.15.</summary>
[Flags]
public enum TypeAttributes : uint
{
    VisibilityMask         = 0x00000007,
    NotPublic              = 0x00000000,
    Public                 = 0x00000001,
    NestedPublic           = 0x00000002,
    NestedPrivate          = 0x00000003,
    NestedFamily           = 0x00000004,
    NestedAssembly         = 0x00000005,
    NestedFamANDAssem      = 0x00000006,
    NestedFamORAssem       = 0x00000007,
    LayoutMask             = 0x00000018,
    AutoLayout             = 0x00000000,
    SequentialLayout       = 0x00000008,
    ExplicitLayout         = 0x00000010,
    ClassSemanticsMask     = 0x00000020,
    Class                  = 0x00000000,
    Interface              = 0x00000020,
    Abstract               = 0x00000080,
    Sealed                 = 0x00000100,
    SpecialName            = 0x00000400,
    Import                 = 0x00001000,
    Serializable           = 0x00002000,
    BeforeFieldInit        = 0x00100000,
    Forwarder              = 0x00200000,
    RTSpecialName          = 0x00000800,
    HasSecurity            = 0x00040000,
}

/// <summary>Field attribute bitmasks from ECMA-335 §II.23.1.5.</summary>
[Flags]
public enum FieldAttributes : ushort
{
    FieldAccessMask        = 0x0007,
    CompilerControlled     = 0x0000,
    Private                = 0x0001,
    FamANDAssem            = 0x0002,
    Assembly               = 0x0003,
    Family                 = 0x0004,
    FamORAssem             = 0x0005,
    Public                 = 0x0006,
    Static                 = 0x0010,
    InitOnly               = 0x0020,
    Literal                = 0x0040,
    NotSerialized          = 0x0080,
    SpecialName            = 0x0200,
    PInvokeImpl            = 0x2000,
    RTSpecialName          = 0x0400,
    HasFieldMarshal        = 0x1000,
    HasDefault             = 0x8000,
    HasFieldRVA            = 0x0100,
}
