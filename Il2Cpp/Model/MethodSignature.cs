// MethodSignature — resolved method signature with parameter types and return type.
// Used to determine MethodInfo null arg position and reconstruct call expressions.

using System.Collections.Generic;

namespace Rosetta.Model;

/// <summary>
/// A fully resolved method signature with return type, parameter names, and types.
/// </summary>
public sealed class MethodSignature
{
    /// <summary>Method name from metadata strings.</summary>
    public required string Name { get; init; }

    /// <summary>Resolved return type name (e.g., "System.Void", "System.String").</summary>
    public required string ReturnTypeName { get; init; }

    /// <summary>Number of physical FP registers consumed by the return type (0 for scalar/GP). Vector3 = 3.</summary>
    public required int ReturnHfaSize { get; init; }

    /// <summary>Instance field names of the return type's HFA struct (from metadata). null if non-HFA.</summary>
    public string[]? ReturnHfaFieldNames { get; init; }

    /// <summary>Whether this method is static (no 'this' pointer in x0).</summary>
    public required bool IsStatic { get; init; }

    /// <summary>Number of C# parameters (excludes 'this' and MethodInfo).</summary>
    public required int ParameterCount { get; init; }

    /// <summary>Resolved parameter list.</summary>
    public List<ParamEntry> Parameters { get; } = new();

    /// <summary>
    /// Total ARM64 register arguments consumed:
    ///   - Instance method: this + params + MethodInfo* = ParameterCount + 2
    ///   - Static method: params + MethodInfo* = ParameterCount + 1
    /// The MethodInfo* is always the LAST argument register.
    /// </summary>
    public int TotalArmArgCount => ParameterCount + (IsStatic ? 1 : 2);

    /// <summary>
    /// ARM64 register index of the MethodInfo* argument (always last).
    /// e.g., static with 2 params → MethodInfo* in x2
    ///        instance with 2 params → MethodInfo* in x3
    /// </summary>
    public int MethodInfoRegIndex => TotalArmArgCount - 1;

    /// <summary>
    /// Check if a specific ARM64 register index is the MethodInfo* argument.
    /// This is always set to null/0 in IL2CPP calls.
    /// </summary>
    public bool IsMethodInfoReg(int regIndex) => regIndex == MethodInfoRegIndex;

    /// <summary>A single parameter entry.</summary>
    public sealed class ParamEntry
    {
        public required string Name { get; init; }
        public required string TypeName { get; init; }
        public int TypeIndex { get; init; } = -1;
        public int HfaSize { get; init; } = 0;
        public bool IsByRef { get; init; } = false;
        public bool IsOut { get; init; } = false;
        public bool IsIn { get; init; } = false;

        public override string ToString() => $"{TypeName} {Name}";
    }

    public override string ToString()
    {
        var paramStr = string.Join(", ", Parameters);
        return $"{ReturnTypeName} {Name}({paramStr})";
    }
}
