using Rosetta.Binary;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.Resolve;

/// <summary>
/// Classifies virtual addresses into ELF section categories for annotation.
///
/// When an ADRP+LDR target cannot be resolved to a specific metadata token,
/// string literal, or method pointer, this classifier provides a meaningful
/// section-based label instead of the raw hex address.
///
/// Source Evidence:
///   - .bss: Zero-initialized data. Contains IL2CPP metadata usage variables
///     (RuntimeClass*, RuntimeType*, MethodInfo*, String_t*) that start as
///     encoded tokens and are resolved at runtime.
///     Source: MetadataUsageWriter.cs line 42-67, CodeWriterExtensions.cs:273
///
///   - .data.rel.ro / .data: Relocated data containing function pointers,
///     vtables, and Il2CppCodeGenModule arrays.
///     Source: CodeRegistrationWriter.cs line 104
///
///   - .rodata: Read-only constant data (string constants, literal pools).
///
///   - .gcc_except_table: C++ exception handling LSDA (Language-Specific Data Area).
///     Source: Clang's personality routine (__gxx_personality_v0)
///
///   - .got: Global Offset Table — PIC indirection (should be resolved via
///     GotIndirectionResolver before reaching this classifier).
///
///   - il2cpp/text: Compiled code section — addresses here are code pointers.
/// </summary>
public sealed class ElfSectionClassifier
{
    private readonly IBinaryParser _elf;

    public ElfSectionClassifier(IBinaryParser elf)
    {
        _elf = elf;
    }

    /// <summary>
    /// Classify a virtual address into a section-based annotation.
    /// Returns null if the address doesn't fall into any known section.
    /// </summary>
    public AddressAnnotation? Classify(ulong va)
    {
        // .bss — metadata usage variables (class info, type info, method info, strings)
        if (IsInSection(_elf.BssSection, va))
        {
            return new AddressAnnotation
            {
                Address = va,
                Kind = AddressKind.BssVariable,
                Label = "metadata_var",
            };
        }

        // .data — global data (relocated pointers, vtables, static fields)
        if (IsInSection(_elf.DataSection, va))
        {
            return new AddressAnnotation
            {
                Address = va,
                Kind = AddressKind.DataPointer,
                Label = "data_ptr",
            };
        }

        // .rodata — read-only constants (string tables, literal pools)
        if (IsInSection(_elf.RoDataSection, va))
        {
            return new AddressAnnotation
            {
                Address = va,
                Kind = AddressKind.ReadOnlyData,
                Label = "rodata",
            };
        }

        // .gcc_except_table — C++ exception LSDA
        if (IsInSection(_elf.GccExceptTableSection, va))
        {
            return new AddressAnnotation
            {
                Address = va,
                Kind = AddressKind.ExceptionTable,
                Label = "exception_lsda",
            };
        }

        // .got — GOT (should have been resolved via GotIndirectionResolver already)
        if (IsInSection(_elf.GotSection, va))
        {
            return new AddressAnnotation
            {
                Address = va,
                Kind = AddressKind.GotEntry,
                Label = "got_entry",
            };
        }

        // Code section — look for any section that contains executable code
        foreach (var sh in _elf.SectionHeaders)
        {
            if ((sh.Flags & 0x4) != 0 && IsInSection(sh, va)) // SHF_EXECINSTR
            {
                return new AddressAnnotation
                {
                    Address = va,
                    Kind = AddressKind.CodePointer,
                    Label = "code_ptr",
                };
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"    ElfSectionClassifier: VA=0x{va:X} → no match");
        return null;
    }

    private static bool IsInSection(BinarySectionHeader? section, ulong va)
    {
        if (section == null) return false;
        return va >= section.Value.VirtualAddr && va < section.Value.VirtualAddr + section.Value.Size;
    }

    // Overload for non-nullable
    private static bool IsInSection(BinarySectionHeader section, ulong va)
    {
        return va >= section.VirtualAddr && va < section.VirtualAddr + section.Size;
    }
}
