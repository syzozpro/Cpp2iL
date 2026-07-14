using Rosetta.Analysis.Resolve;
using Rosetta.Binary;
using Rosetta.Lifter.ClangRules;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.Disassembly;

/// <summary>
/// Annotates unconditional B (branch) instructions with semantic labels.
///
/// ARM64 B instructions serve two purposes in IL2CPP codegen:
///
/// 1. **Tail calls** (inter-method): Clang optimizes `BL target; RET` into `B target`.
///    Source: MethodBodyWriter.cs L1373 (Code.Br emits goto, but tail calls are
///    a Clang backend optimization when the caller's epilogue is trivial).
///    Resolution: Same as BL — look up target in method/call maps.
///
/// 2. **Control flow** (intra-method): C++ goto statements emitted by IL2CPP's Labeler.
///    Source: Labeler.cs L62-65: ForJump → "goto IL_XXXX;"
///    These compile to B instructions within the same method.
///    Resolution: Compute signed offset from instruction address to target.
///
/// Discrimination between the two categories is simple: if the target falls within
/// the current method's address range [methodStart..methodEnd], it's intra-method
/// control flow. Otherwise, it's a tail call.
/// </summary>
public static class BranchAnnotator
{
    /// <summary>
    /// Annotate a B instruction.
    /// </summary>
    /// <param name="inst">The decoded B instruction.</param>
    /// <param name="methodStartVA">Start VA of the current method.</param>
    /// <param name="methodEndVA">End VA of the current method (exclusive).</param>
    /// <param name="callResolver">Resolver for inter-method targets.</param>
    /// <param name="instructions">All decoded instructions in this method (for call-site classification).</param>
    /// <param name="instructionIndex">Index of this B instruction in the array.</param>
    /// <returns>Annotation string (e.g., "  ; → method_name" or "  ; → +0x34"), or empty string.</returns>
    public static string Annotate(
        Arm64Instruction inst,
        ulong methodStartVA,
        ulong methodEndVA,
        CallResolver callResolver,
        Arm64Instruction[] instructions,
        int instructionIndex)
    {
        if (inst.Opcode != Arm64Opcode.B)
            return "";

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    BranchAnnotator: B at 0x{inst.Address:X} target=0x{inst.Immediate:X}");

        ulong target = (ulong)inst.Immediate;

        // ── Category 1: Intra-method control flow ──
        // Source: Labeler.cs L62-65 — IL2CPP emits goto IL_XXXX;
        // Clang compiles these to B instructions within the method body.
        if (target >= methodStartVA && target < methodEndVA)
        {
            return AnnotateIntraMethod(inst.Address, target);
        }

        // ── Category 2: Inter-method tail call ──
        // Source: Clang optimizer — BL target; RET → B target
        // The target is a known method or runtime function.
        return AnnotateTailCall(target, callResolver, instructions, instructionIndex);
    }

    /// <summary>
    /// Annotate an intra-method branch with relative offset.
    ///
    /// Source: Labeler.cs L109-143 — FormatOffset produces "IL_XXXX" labels.
    /// In disassembly, we show the signed byte offset to convey jump direction.
    ///
    /// Forward jumps (+) typically indicate:
    ///   - if/else skip-over (Source: MethodBodyWriter.cs L4352: GenerateRightBranch)
    ///   - loop exit
    ///   - exception handler skip
    ///
    /// Backward jumps (-) typically indicate:
    ///   - loop back-edge (Source: MethodBodyWriter.cs L829: goto end-of-loop)
    ///   - switch fallthrough
    /// </summary>
    private static string AnnotateIntraMethod(ulong sourceAddr, ulong targetAddr)
    {
        long offset = (long)(targetAddr - sourceAddr);
        string sign = offset >= 0 ? "+" : "";
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → Intra-method B offset {sign}0x{Math.Abs(offset):X}");
        return $"  ; → {sign}0x{Math.Abs(offset):X}";
    }

    /// <summary>
    /// Annotate an inter-method tail call via CallResolver.
    ///
    /// Source: Clang's ARM64 backend applies tail-call optimization when:
    ///   1. The callee's signature is compatible
    ///   2. The caller's stack frame can be unwound before the jump
    ///   3. No work remains after the call (last statement before return)
    ///
    /// This is identical to BL resolution — we reuse the CallResolver and
    /// CallSiteClassifier infrastructure.
    /// </summary>
    private static string AnnotateTailCall(
        ulong target,
        CallResolver callResolver,
        Arm64Instruction[] instructions,
        int instructionIndex)
    {
        var resolved = callResolver.TryResolve(target);
        if (resolved != null)
        {
            string name = resolved.MethodName ?? "";

            // If generic "il2cpp_runtime_helper", try call-site classification
            if (name == "il2cpp_runtime_helper")
            {
                string? betterName = CallSiteClassifier.ClassifyFromContext(instructions, instructionIndex);
                if (betterName != null)
                    name = betterName;
            }

            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → Tail-call B to {name}");
            return $"  ; → {name}";
        }

        return "";
    }
}
