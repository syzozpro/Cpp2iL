namespace Rosetta.Lifter.IR.RuntimeHelpers;

/// <summary>
/// Cross-architecture register constants for runtime helper classifiers.
/// ARM64: SP = register 31, LR = register 30
/// ARM32: SP = register 13, LR = register 14
/// </summary>
internal static class IrRegisterConstants
{
    /// <summary>Returns true if the register value represents the stack pointer on either ARM64 or ARM32.</summary>
    internal static bool IsStackPointer(long regValue) => regValue == 31 || regValue == 13;
}
