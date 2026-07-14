namespace Rosetta.Analysis.Resolve;

/// <summary>What kind of metadata entity an address represents.</summary>
public enum AddressKind
{
    Unknown,
    ClassInitFlag,         // LDRB from .bss — bool check for class_init
    RuntimeClass,          // Il2CppClass* pointer (TypeInfo)
    RuntimeType,           // Il2CppType* pointer
    MethodInfo,            // MethodInfo* pointer
    FieldInfo,             // FieldInfo* pointer
    FieldRva,              // FieldRva — raw initialization data for array literals (token type 7)
    StringLiteral,         // String_t* pointer
    MethodRef,             // Generic method ref pointer
    MethodPointer,         // Direct code address (BL target)

    // Section-based classifications (Gap 3 resolution)
    BssVariable,           // .bss — metadata usage variable (uninitialized at compile time)
    DataPointer,           // .data — relocated pointer (vtable, function pointer table)
    ReadOnlyData,          // .rodata — read-only constant data
    ExceptionTable,        // .gcc_except_table — C++ exception LSDA
    GotEntry,              // .got — Global Offset Table entry
    CodePointer,           // .text/il2cpp — compiled code address
}

/// <summary>Annotation for a resolved address: what it means in the metadata world.</summary>
public sealed class AddressAnnotation
{
    public ulong Address { get; init; }
    public AddressKind Kind { get; init; }
    public int MetadataIndex { get; init; } = -1;
    public string Label { get; init; } = "";

    public override string ToString() => Kind switch
    {
        AddressKind.ClassInitFlag  => "class_init_flag",
        AddressKind.RuntimeClass   => $"RuntimeClass({Label})",
        AddressKind.RuntimeType    => $"RuntimeType({Label})",
        AddressKind.MethodInfo     => $"MethodInfo({Label})",
        AddressKind.FieldInfo      => $"FieldInfo({Label})",
        AddressKind.FieldRva       => $"FieldRva({Label})",
        AddressKind.StringLiteral  => $"\"{Label}\"",
        AddressKind.MethodRef      => $"MethodRef({Label})",
        AddressKind.MethodPointer  => $"→ {Label}",
        AddressKind.BssVariable    => $"bss:{Label}",
        AddressKind.DataPointer    => $"data:{Label}",
        AddressKind.ReadOnlyData   => $"rodata:{Label}",
        AddressKind.ExceptionTable => $"lsda:{Label}",
        AddressKind.GotEntry       => $"got:{Label}",
        AddressKind.CodePointer    => $"code:{Label}",
        _ => $"0x{Address:X}",
    };
}

