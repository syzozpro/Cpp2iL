namespace Rosetta.Lifter.IR.Nodes;

/// <summary>
/// A single IR instruction — one atomic, platform-agnostic operation.
///
/// Design:
///   - Address: preserved from the original ARM64 instruction for traceability
///   - Opcode: the IR operation (see <see cref="IrOpcode"/>)
///   - Destination: where the result goes (null for void ops like Store, Branch, Return)
///   - Sources: input operands (0-4 depending on opcode)
///   - Annotation: human-readable context from disassembly (resolved names, metadata)
///
/// Scalability: This is the atom for CFG basic blocks. Each block will be a
/// List&lt;IrInstruction&gt;, and data-flow analysis walks the Source→Destination chains.
/// </summary>
public sealed class IrInstruction
{
    /// <summary>Original ARM64 virtual address (for traceability/debugging).</summary>
    public ulong Address { get; init; }

    /// <summary>The IR operation.</summary>
    public IrOpcode Opcode { get; set; }

    /// <summary>
    /// Destination operand (where the result is stored).
    /// Null for void operations (Store, Branch, Return, RuntimeInit, etc.).
    /// </summary>
    public IrOperand? Destination { get; set; }

    /// <summary>
    /// Source operands. Length depends on opcode:
    ///   0: Nop, Return (void), StackAlloc/Free
    ///   1: Assign, Neg, FNeg, LoadImmediate, Branch, Return (value)
    ///   2: Add, Sub, Load, Store, Compare, ConditionalBranch
    ///   3: Select, Call (target + arg count encoded), StoreField
    ///   4+: rare (Call with inline args)
    /// </summary>
    public IrOperand[] Sources { get; init; } = [];

    /// <summary>
    /// Human-readable annotation for display/debugging.
    /// Examples: "→ Debug.Log", "class_init_flag", "RuntimeClass(UnityEngine.Debug)"
    /// </summary>
    public string? Annotation { get; set; }

    /// <summary>
    /// Number of ARM64 instructions that were collapsed into this single IR op.
    /// 1 = direct 1:1 mapping, 2+ = pattern was recognized and fused.
    /// Useful for mapping IR back to disassembly ranges.
    /// </summary>
    public int CollapsedCount { get; init; } = 1;

    /// <summary>
    /// Resolved return type name from metadata (e.g., "System.String", "System.Int32").
    /// Set by the lifter for Call instructions when the method's return type is known.
    /// Null for void calls, unresolved calls, and non-call instructions.
    /// </summary>
    public string? ResultType { get; set; }

    /// <summary>
    /// For Branch and ConditionalBranch instructions, specifies the condition evaluation.
    /// </summary>
    public IrBranchCondition Condition { get; init; } = IrBranchCondition.None;

    /// <summary>
    /// For Call instructions, the exact method index in the metadata.
    /// Null if this is an indirect call or unresolved runtime helper.
    /// </summary>
    public int? TargetMethodIndex { get; set; }

    /// <summary>
    /// Metadata usage kind/index carried from resolved metadata loads.
    /// For RuntimeClass/RuntimeType, MetadataIndex is the Il2CppType index from metadata usage.
    /// </summary>
    public Rosetta.Analysis.Resolve.AddressKind MetadataKind { get; set; } = Rosetta.Analysis.Resolve.AddressKind.Unknown;
    public int MetadataIndex { get; set; } = -1;

    /// <summary>
    /// Exact TypeDefinition index for value type boxing, resolved from metadata usage.
    /// </summary>
    public int BoxedTypeDefIndex { get; set; } = -1;

    /// <summary>
    /// Strongly-typed semantic tag replacing string annotations for IL2CPP boilerplate constructs.
    /// </summary>
    public IrSemanticTag SemanticTag { get; set; } = IrSemanticTag.None;

    /// <summary>
    /// True if this Call instruction targets a function that never returns
    /// (e.g., __cxa_throw, __cxa_rethrow, abort). Used by the CFG builder
    /// to avoid creating phantom fallthrough edges after noreturn calls.
    /// </summary>
    public bool IsNoReturn { get; set; }

    /// <summary>
    /// Known C/C++ ABI functions that never return. These are universal invariants
    /// defined by the Itanium C++ ABI and POSIX standards.
    /// </summary>
    private static readonly HashSet<string> NoReturnFunctions = new(StringComparer.Ordinal)
    {
        "__cxa_throw",
        "__cxa_rethrow",
        "abort",
        "_exit",
        "__assert_fail",
        "il2cpp_codegen_raise_exception",
        "il2cpp_raise_exception",
    };

    /// <summary>
    /// Set of register var-IDs implicitly clobbered by this instruction (ARM64 ABI).
    /// For Call/IndirectCall: x0-x18 (IDs 0-18) are caller-saved and may be destroyed.
    /// The SSA builder reads this to create new versions after calls, preventing
    /// pre-call values from leaking into post-call uses.
    /// Null for non-call instructions.
    /// </summary>
    public IReadOnlySet<int>? ClobberedRegisters { get; init; }

    /// <summary>ARM64 caller-saved registers (x0-x18, v0-v7, v16-v31). Shared instance for all calls.</summary>
    public static readonly IReadOnlySet<int> Arm64CallerSavedRegisters =
        new HashSet<int>(Enumerable.Range(0, 19).Concat(Enumerable.Range(100, 8)).Concat(Enumerable.Range(116, 16)));

    /// <summary>ARM64 caller-saved registers excluding the primary GP return register (x1-x18, v0-v7, v16-v31).
    /// Used for void calls where x0 retains the caller's value (not used as return).</summary>
    public static readonly IReadOnlySet<int> Arm64CallerSavedRegistersNoReturn =
        new HashSet<int>(Enumerable.Range(1, 18).Concat(Enumerable.Range(100, 8)).Concat(Enumerable.Range(116, 16)));

    /// <summary>ARM32 AAPCS caller-saved registers: R0-R3, R12 (IP), LR (R14), S0-S15.
    /// Used for BLX <reg> indirect calls in Thumb2.</summary>
    public static readonly IReadOnlySet<int> Thumb2CallerSavedRegisters =
        new HashSet<int>(Enumerable.Range(0, 4).Append(12).Append(14).Concat(Enumerable.Range(100, 16)));


    // ─── Factory Methods (common patterns) ──────────────────────────────────

    public static IrInstruction CreateAssign(ulong addr, IrOperand dst, IrOperand src)
        => new() { Address = addr, Opcode = IrOpcode.Assign, Destination = dst, Sources = [src] };

    public static IrInstruction CreateLoadImm(ulong addr, IrOperand dst, IrOperand imm)
        => new() { Address = addr, Opcode = IrOpcode.LoadImmediate, Destination = dst, Sources = [imm] };

    public static IrInstruction CreateBinOp(ulong addr, IrOpcode op, IrOperand dst, IrOperand a, IrOperand b)
        => new() { Address = addr, Opcode = op, Destination = dst, Sources = [a, b] };

    public static IrInstruction CreateLoad(ulong addr, IrOperand dst, IrOperand mem)
        => new() { Address = addr, Opcode = IrOpcode.Load, Destination = dst, Sources = [mem] };

    /// <summary>Register-indexed load: dst = load [base + indexReg]. Index register is Sources[1].</summary>
    public static IrInstruction CreateLoadIndexed(ulong addr, IrOperand dst, IrOperand mem, IrOperand indexReg)
        => new() { Address = addr, Opcode = IrOpcode.Load, Destination = dst, Sources = [mem, indexReg] };

    public static IrInstruction CreateStore(ulong addr, IrOperand mem, IrOperand value)
        => new() { Address = addr, Opcode = IrOpcode.Store, Destination = null, Sources = [mem, value] };

    public static IrInstruction CreateBranch(ulong addr, IrOperand target)
        => new() { Address = addr, Opcode = IrOpcode.Branch, Destination = null, Sources = [target] };

    public static IrInstruction CreateCondBranch(ulong addr, IrOperand cond, IrOperand target)
        => new() { Address = addr, Opcode = IrOpcode.ConditionalBranch, Destination = null, Sources = [cond, target] };

    public static IrInstruction CreateCall(ulong addr, IrOperand? dst, IrOperand target, string? annotation = null)
        => new() { Address = addr, Opcode = IrOpcode.Call, Destination = dst, Sources = [target], Annotation = annotation };

    public static IrInstruction CreateReturn(ulong addr, IrOperand? value = null)
        => new() { Address = addr, Opcode = IrOpcode.Return, Destination = null, Sources = value.HasValue ? [value.Value] : [] };

    public static IrInstruction CreateNop(ulong addr)
        => new() { Address = addr, Opcode = IrOpcode.Nop };

    /// <summary>
    /// Check if the given annotation refers to a known noreturn function.
    /// </summary>
    public static bool IsKnownNoReturn(string? annotation)
        => annotation != null && NoReturnFunctions.Contains(annotation);

    // ─── Display ────────────────────────────────────────────────────────────

    public override string ToString()
    {
        string dstStr = Destination.HasValue ? $"{Destination.Value} = " : "";
        string srcStr = Sources.Length > 0
            ? string.Join(", ", Sources.Select(s => s.ToString()))
            : "";
        string ann = Annotation != null ? $"  ; {Annotation}" : "";

        return Opcode switch
        {
            IrOpcode.Nop => $"    nop{ann}",
            IrOpcode.Assign => $"    {dstStr}{srcStr}{ann}",
            IrOpcode.LoadImmediate => $"    {dstStr}{srcStr}{ann}",
            IrOpcode.Load => $"    {dstStr}load {srcStr}{ann}",
            IrOpcode.Store => $"    store {srcStr}{ann}",
            IrOpcode.LoadField => $"    {dstStr}load_field {srcStr}{ann}",
            IrOpcode.StoreField => $"    store_field {srcStr}{ann}",
            IrOpcode.LoadAddress => $"    {dstStr}addr {srcStr}{ann}",

            IrOpcode.Add or IrOpcode.Sub or IrOpcode.Mul or IrOpcode.SDiv or IrOpcode.UDiv
                => $"    {dstStr}{OpcodeStr()} {srcStr}{ann}",

            IrOpcode.And or IrOpcode.Or or IrOpcode.Xor or IrOpcode.Shl or IrOpcode.Shr or IrOpcode.Sar or IrOpcode.Ror
                => $"    {dstStr}{OpcodeStr()} {srcStr}{ann}",

            IrOpcode.Neg or IrOpcode.Not
                => $"    {dstStr}{OpcodeStr()} {srcStr}{ann}",

            IrOpcode.FAdd or IrOpcode.FSub or IrOpcode.FMul or IrOpcode.FDiv
            or IrOpcode.FNeg or IrOpcode.FAbs or IrOpcode.FSqrt or IrOpcode.FNMul
            or IrOpcode.FMin or IrOpcode.FMax or IrOpcode.FRound
                => $"    {dstStr}{OpcodeStr()} {srcStr}{ann}",

            IrOpcode.SignedIntToFloat or IrOpcode.UnsignedIntToFloat
            or IrOpcode.FloatToSignedInt or IrOpcode.FloatToUnsignedInt
            or IrOpcode.FloatExtend or IrOpcode.FloatTruncate or IrOpcode.Bitcast
                => $"    {dstStr}{OpcodeStr()} {srcStr}{ann}",

            IrOpcode.Compare or IrOpcode.FCompare or IrOpcode.Test
                => $"    {OpcodeStr()} {srcStr}{ann}",

            IrOpcode.Select or IrOpcode.SelectInc or IrOpcode.SelectInv or IrOpcode.SelectNeg
                => $"    {dstStr}{OpcodeStr()} {srcStr}{ann}",

            IrOpcode.Branch => $"    goto {srcStr}{ann}",
            IrOpcode.ConditionalBranch => FormatConditionalBranch(ann),
            IrOpcode.Call => $"    {dstStr}call {srcStr}{ann}",
            IrOpcode.Return => Sources.Length > 0 ? $"    return {srcStr}{ann}" : $"    return{ann}",
            IrOpcode.TailCall => $"    tailcall {srcStr}{ann}",
            IrOpcode.IndirectBranch => $"    goto *{srcStr}{ann}",
            IrOpcode.IndirectCall => $"    {dstStr}icall {srcStr}{ann}",

            IrOpcode.RuntimeInit => $"    runtime_init {srcStr}{ann}",
            IrOpcode.ClassInit => $"    class_init {srcStr}{ann}",
            IrOpcode.WriteBarrier => $"    write_barrier {srcStr}{ann}",
            IrOpcode.Box => $"    {dstStr}box {srcStr}{ann}",
            IrOpcode.IsInst => $"    {dstStr}isinst {srcStr}{ann}",
            IrOpcode.NullCheck => $"    null_check {srcStr}{ann}",
            IrOpcode.NewObject => $"    {dstStr}new_obj {srcStr}{ann}",
            IrOpcode.NewArray => $"    {dstStr}new_array {srcStr}{ann}",
            IrOpcode.RuntimeHelper => $"    {dstStr}rt_helper {srcStr}{ann}",

            IrOpcode.StackAlloc => $"    stack_alloc {srcStr}{ann}",
            IrOpcode.StackFree => $"    stack_free {srcStr}{ann}",

            IrOpcode.SMulWide or IrOpcode.UMulWide or IrOpcode.SMulHigh or IrOpcode.UMulHigh
                => $"    {dstStr}{OpcodeStr()} {srcStr}{ann}",

            IrOpcode.BitfieldExtractUnsigned or IrOpcode.BitfieldExtractSigned or IrOpcode.BitfieldInsert
                => $"    {dstStr}{OpcodeStr()} {srcStr}{ann}",

            IrOpcode.SignExtend8 or IrOpcode.SignExtend16 or IrOpcode.SignExtend32
            or IrOpcode.ZeroExtend8 or IrOpcode.ZeroExtend16
                => $"    {dstStr}{OpcodeStr()} {srcStr}{ann}",

            _ => $"    {dstStr}{OpcodeStr()} {srcStr}{ann}"
        };
    }

    /// <summary>
    /// Format a conditional branch with correct branch sense.
    /// CBZ: "if (!reg) goto" — branch taken when zero (null check)
    /// CBNZ/TBZ/TBNZ/B.cond: "if (reg/cond) goto" — standard sense
    /// </summary>
    private string FormatConditionalBranch(string ann)
    {
        string target = Sources.Length > 1 ? Sources[1].ToString() : "?";
        string cond = Sources.Length > 0 ? Sources[0].ToString() : "?";

        if (Condition == IrBranchCondition.Zero || Condition == IrBranchCondition.BitZero)
            return $"    if (!{cond}) goto {target}{ann}";

        return $"    if ({cond}) goto {target}{ann}";
    }

    private string OpcodeStr() => Opcode.ToString().ToLowerInvariant();
}

public enum IrBranchCondition : byte
{
    None = 0,
    Zero,
    NotZero,
    BitZero,
    BitNotZero
}

public enum IrSemanticTag : byte
{
    None = 0,
    ClassInit,
    MethodRef,
    RuntimeHelper,
    VTableLoad
}
