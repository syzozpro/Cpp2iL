namespace Rosetta.Lifter.IR.Nodes;

/// <summary>
/// Platform-agnostic IR opcodes. These represent pure logical operations
/// with no hardware-specific semantics (no CPU flags, no specific registers).
///
/// Design: Each opcode maps to exactly one semantic action.
/// This is intentionally a superset — later passes (DCE, const-fold) prune unused ops.
///
/// Scalability note: When connecting to CFG, each IR instruction maps 1:1 to a node
/// in the data-flow graph. Branch/Call/Return ops become CFG edges.
/// </summary>
public enum IrOpcode : byte
{
    // ─── Sentinel ───────────────────────────────────────────────────────────
    Nop = 0,
    Unknown,

    // ─── Data Movement ──────────────────────────────────────────────────────
    /// <summary>dst = src (register-to-register copy)</summary>
    Assign,
    /// <summary>dst = immediate_value</summary>
    LoadImmediate,
    /// <summary>dst = [address] (memory read)</summary>
    Load,
    /// <summary>[address] = src (memory write)</summary>
    Store,
    /// <summary>dst = [base + offset] — structured field/member access</summary>
    LoadField,
    /// <summary>[base + offset] = src — structured field/member write</summary>
    StoreField,
    /// <summary>dst = address_of(page + offset) — resolved ADRP+ADD pair</summary>
    LoadAddress,

    // ─── Integer Arithmetic ─────────────────────────────────────────────────
    /// <summary>dst = a + b</summary>
    Add,
    /// <summary>dst = a - b</summary>
    Sub,
    /// <summary>dst = a * b</summary>
    Mul,
    /// <summary>dst = a / b (signed)</summary>
    SDiv,
    /// <summary>dst = a / b (unsigned)</summary>
    UDiv,
    /// <summary>dst = -a</summary>
    Neg,

    // ─── Bitwise ────────────────────────────────────────────────────────────
    /// <summary>dst = a & b</summary>
    And,
    /// <summary>dst = a | b</summary>
    Or,
    /// <summary>dst = a ^ b</summary>
    Xor,
    /// <summary>dst = ~a</summary>
    Not,
    /// <summary>dst = a << b (logical shift left)</summary>
    Shl,
    /// <summary>dst = a >> b (logical shift right, unsigned)</summary>
    Shr,
    /// <summary>dst = a >> b (arithmetic shift right, signed)</summary>
    Sar,
    /// <summary>dst = a >>> b (rotate right)</summary>
    Ror,

    // ─── Floating-Point Arithmetic ──────────────────────────────────────────
    /// <summary>dst = a + b (float)</summary>
    FAdd,
    /// <summary>dst = a - b (float)</summary>
    FSub,
    /// <summary>dst = a * b (float)</summary>
    FMul,
    /// <summary>dst = a / b (float)</summary>
    FDiv,
    /// <summary>dst = -a (float)</summary>
    FNeg,
    /// <summary>dst = |a| (float)</summary>
    FAbs,
    /// <summary>dst = sqrt(a)</summary>
    FSqrt,
    /// <summary>dst = -(a * b) (fused negate-multiply)</summary>
    FNMul,
    /// <summary>dst = min(a, b) (float)</summary>
    FMin,
    /// <summary>dst = max(a, b) (float)</summary>
    FMax,
    /// <summary>dst = round(a) with specified rounding mode</summary>
    FRound,
    /// <summary>dst = c + a * b (fused multiply-add)</summary>
    FMulAdd,
    /// <summary>dst = c - a * b (fused multiply-subtract)</summary>
    FMulSub,

    // ─── Float ↔ Int Conversion ─────────────────────────────────────────────
    /// <summary>dst = (float)int_src — signed int to float</summary>
    SignedIntToFloat,
    /// <summary>dst = (float)uint_src — unsigned int to float</summary>
    UnsignedIntToFloat,
    /// <summary>dst = (int)float_src — float to signed int (truncate)</summary>
    FloatToSignedInt,
    /// <summary>dst = (uint)float_src — float to unsigned int (truncate)</summary>
    FloatToUnsignedInt,
    /// <summary>dst = (double)single_src — extend precision</summary>
    FloatExtend,
    /// <summary>dst = (single)double_src — truncate precision</summary>
    FloatTruncate,
    /// <summary>dst = bitcast(src) — reinterpret bits (e.g., float as int)</summary>
    Bitcast,

    // ─── Comparison ─────────────────────────────────────────────────────────
    /// <summary>flags = compare(a, b) — integer compare</summary>
    Compare,
    /// <summary>flags = compare(a, b) — float compare</summary>
    FCompare,
    /// <summary>flags = a & b (test bits, discard result)</summary>
    Test,

    // ─── Conditional Select ─────────────────────────────────────────────────
    /// <summary>dst = condition ? a : b</summary>
    Select,
    /// <summary>dst = condition ? a : b+1</summary>
    SelectInc,
    /// <summary>dst = condition ? a : ~b</summary>
    SelectInv,
    /// <summary>dst = condition ? a : -b</summary>
    SelectNeg,

    // ─── Control Flow ───────────────────────────────────────────────────────
    /// <summary>goto label</summary>
    Branch,
    /// <summary>if (condition) goto label</summary>
    ConditionalBranch,
    /// <summary>result = call target(args...)</summary>
    Call,
    /// <summary>return [value]</summary>
    Return,
    /// <summary>goto target (tail-call optimization — B instead of BL+RET)</summary>
    TailCall,
    /// <summary>goto *register (indirect branch — switch tables, vtable dispatch)</summary>
    IndirectBranch,
    /// <summary>result = call *register(args...)</summary>
    IndirectCall,

    // ─── IL2CPP Runtime Helpers ─────────────────────────────────────────────
    /// <summary>il2cpp_codegen_initialize_runtime_metadata(klass)</summary>
    RuntimeInit,
    /// <summary>il2cpp_codegen_runtime_class_init(klass)</summary>
    ClassInit,
    /// <summary>Il2CppCodeGenWriteBarrier(&dst, src)</summary>
    WriteBarrier,
    /// <summary>result = Box(type, &value)</summary>
    Box,
    /// <summary>result = IsInst(obj, type)</summary>
    IsInst,
    /// <summary>if (obj == null) throw NullReferenceException</summary>
    NullCheck,
    /// <summary>result = il2cpp_codegen_object_new(klass)</summary>
    NewObject,
    /// <summary>result = SZArrayNew(klass, length)</summary>
    NewArray,
    /// <summary>Generic IL2CPP runtime helper call (fallback)</summary>
    RuntimeHelper,

    // ─── Stack Frame (collapsed from STP/LDP prologue/epilogue) ─────────────
    /// <summary>Prologue: allocate stack frame and save registers</summary>
    StackAlloc,
    /// <summary>Epilogue: restore registers and deallocate stack frame</summary>
    StackFree,

    // ─── Wide Integer Ops ───────────────────────────────────────────────────
    /// <summary>dst = (int64)(int32)a * (int32)b — signed widening multiply</summary>
    SMulWide,
    /// <summary>dst = (uint64)(uint32)a * (uint32)b — unsigned widening multiply</summary>
    UMulWide,
    /// <summary>dst = (a * b) >> 64 — signed multiply high</summary>
    SMulHigh,
    /// <summary>dst = (a * b) >> 64 — unsigned multiply high</summary>
    UMulHigh,

    // ─── Bitfield ───────────────────────────────────────────────────────────
    /// <summary>dst = ExtractBits(src, lsb, width) — unsigned bitfield extract</summary>
    BitfieldExtractUnsigned,
    /// <summary>dst = SignExtract(src, lsb, width) — signed bitfield extract</summary>
    BitfieldExtractSigned,
    /// <summary>dst = InsertBits(dst, src, lsb, width) — bitfield insert</summary>
    BitfieldInsert,

    // ─── Sign/Zero Extension ────────────────────────────────────────────────
    /// <summary>dst = (int32)(int8)src — sign extend byte</summary>
    SignExtend8,
    /// <summary>dst = (int32)(int16)src — sign extend halfword</summary>
    SignExtend16,
    /// <summary>dst = (int64)(int32)src — sign extend word</summary>
    SignExtend32,
    /// <summary>dst = src & 0xFF — zero extend byte</summary>
    ZeroExtend8,
    /// <summary>dst = src & 0xFFFF — zero extend halfword</summary>
    ZeroExtend16,

    // ─── Future SSA ─────────────────────────────────────────────────────────
    /// <summary>dst = phi(a, b, ...) — SSA merge point (not emitted yet)</summary>
    Phi,
}
