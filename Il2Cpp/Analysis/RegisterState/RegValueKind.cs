namespace Rosetta.Analysis.RegisterState;

/// <summary>
/// What kind of value a register holds.
/// </summary>
public enum RegValueKind
{
    Unknown,           // No info
    This,              // The 'this' pointer (method entry x0 for instance methods)
    Literal,           // Immediate integer value
    StringLiteral,     // Pointer to a string constant
    TypeOf,            // typeof(T) — Il2CppClass* for type T
    MetadataPage,      // Raw il2cpp_metadata_page pointer (base for field lookups)
    FieldValue,        // Loaded from a field (baseReg + offset)
    ArrayRef,          // Reference to an array object
    CallResult,        // Return value of a method call
    ObjectRef,         // Reference to an object (non-this, non-array)
    AddressOf,         // Pointer to SP+offset or similar
    Copied,            // Copied from another register (reg-to-reg move)
}
