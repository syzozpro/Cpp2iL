using System;
using System.Collections.Generic;
using Rosetta.Binary;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Lifter.ClangRules;
using Rosetta.Metadata;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR;

public sealed class IrLifter
{
    private readonly CallResolver _callResolver;
    private readonly IBinaryParser? _elf;
    private readonly byte[]? _binaryBytes;
    private readonly List<IrInstruction> _pending = new(2);

    // ── MOVZ/MOVK deferred chain tracking ──
    // ARM64 compilers interleave MOVZ+MOVK chains across registers for pipeline
    // efficiency. We track the accumulator per register and flush the final
    // assembled value when the chain is complete or the register is consumed.
    private readonly ulong[] _movAccum = new ulong[31];    // accumulated constant bits
    private readonly bool[] _movActive = new bool[31];      // is chain active for this register?
    private readonly bool[] _movIs64 = new bool[31];        // is the chain 64-bit?
    private readonly int[] _movSlotsFilled = new int[31];   // how many 16-bit slots filled (1=MOVZ only, 2-4=+MOVKs)
    private readonly ulong[] _movAddr = new ulong[31];      // address of first instruction in chain

    // ── ADRP page address tracking ──
    // Tracks the last ADRP-computed page address per GP register.
    // Used by LDR_SIMD_IMM to resolve FP constant loads from rodata pages
    // (e.g., Mathf.PI, 2.0f stored in .rodata literal pools).
    private readonly long[] _adrpPageAddr = new long[31];   // VA of page from ADRP
    private readonly bool[] _adrpPageValid = new bool[31];  // whether page addr is valid

    // ── Current method context (set at start of Lift) ──
    private string _currentReturnType = "void";
    private bool _currentIsStatic;


    public IrLifter(CallResolver callResolver, IBinaryParser? elf = null, byte[]? binaryBytes = null)
    {
        _callResolver = callResolver;
        _elf = elf;
        _binaryBytes = binaryBytes;
    }

    public IrMethod Lift(MethodDefinition methodDef, ulong methodVA, Arm64Instruction[] armInsts, string methodName, string? declaringType, string returnType, List<string> parameters, bool isStatic)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"IrLifter.Lift: {declaringType}::{methodName} (VA=0x{methodVA:X}, {armInsts.Length} instrs)");
        var instructions = new List<IrInstruction>(armInsts.Length);
        int collapsed = 0;
        _currentReturnType = returnType;
        _currentIsStatic = isStatic;

        // Reset per-register tracking for this method
        Array.Clear(_movActive);
        Array.Clear(_movSlotsFilled);
        Array.Clear(_adrpPageValid);

        for (int i = 0; i < armInsts.Length; i++)
        {
            var arm = armInsts[i];
            _pending.Clear();
            TranslateInstruction(arm, armInsts, ref i, methodVA, instructions);
            foreach (var ir in _pending)
            {
                instructions.Add(ir);
                if (ir.CollapsedCount > 1)
                    collapsed += (ir.CollapsedCount - 1);
            }
        }

        // Flush any remaining chains at end of method
        _pending.Clear();
        FlushAllMovChains();
        
        foreach (var ir in _pending)
            instructions.Add(ir);

        var result = new IrMethod
        {
            MethodName = methodName, DeclaringType = declaringType, ReturnType = returnType,
            Parameters = parameters, IsStatic = isStatic, EntryAddress = methodVA,
            Token = methodDef.Token, TypeDefIndex = methodDef.DeclaringTypeIndex,
            MethodIndex = methodDef.GlobalIndex,
            Instructions = instructions,
            OriginalInstructionCount = armInsts.Length, CollapsedInstructionCount = collapsed,
            IsArm32 = false
        };
        result.BuildParamMaps();
        
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  IrLifter: Lifted {armInsts.Length} ARM instructions to {instructions.Count} IR instructions");
        return result;
    }

    private void Emit(IrInstruction ir) => _pending.Add(ir);

    /// <summary>
    /// Flush a pending MOVZ/MOVK chain for register rd, emitting the fully-assembled constant.
    /// Called when: (a) the chain completes (all expected slots filled), or
    /// (b) another instruction writes to rd, or (c) rd is consumed as a source.
    /// </summary>
    private void FlushMovChain(int rd)
    {
        if (rd >= 31 || !_movActive[rd]) return;
        _movActive[rd] = false;

        bool is64 = _movIs64[rd];
        byte width = (byte)(is64 ? 64 : 32);
        int slots = _movSlotsFilled[rd];

        // Always emit as integer immediate. Do NOT guess float/double here —
        // the IsFinite heuristic is highly volatile and causes integer bitmasks
        // (e.g., 0x3F800000 = 1.0f) to be falsely emitted as float literals.
        // Let the downstream expression propagator or type-inference engine
        // determine if it should be formatted as a float based on how the
        // register is subsequently used (e.g., FMOV to FP register).
        var source = IrOperand.Immediate((long)_movAccum[rd], width);

        var ir = new IrInstruction
        {
            Address = _movAddr[rd], Opcode = IrOpcode.LoadImmediate,
            Destination = Reg(rd, is64),
            Sources = [source],
            CollapsedCount = slots
        };
        Emit(ir);
    }

    /// <summary>Flush all active chains (e.g., at end of method or before a branch).</summary>
    private void FlushAllMovChains()
    {
        for (int r = 0; r < 31; r++)
            FlushMovChain(r);
    }

    /// <summary>
    /// Check if a register has an active MOVZ/MOVK chain, and determine if a
    /// given instruction's read of that register requires flushing.
    /// </summary>
    private void FlushIfSourced(int reg)
    {
        if (reg < 31 && _movActive[reg])
            FlushMovChain(reg);
    }

    private void TranslateInstruction(Arm64Instruction arm, Arm64Instruction[] armInsts, ref int i, ulong methodVA, List<IrInstruction> allInsts)
    {
        ulong addr = arm.Address;

        // ── Flush active MOVZ/MOVK chains when registers are consumed ──
        // For non-MOV instructions that read GP registers, flush any pending chain.
        // For branches, flush ALL chains (values must be materialized before jumps).
        if (arm.Opcode != Arm64Opcode.MOVZ && arm.Opcode != Arm64Opcode.MOVK && arm.Opcode != Arm64Opcode.NOP)
        {
            // Branches flush all chains
            if (arm.Opcode is Arm64Opcode.B or Arm64Opcode.BL or Arm64Opcode.B_COND
                or Arm64Opcode.CBZ or Arm64Opcode.CBNZ or Arm64Opcode.TBZ or Arm64Opcode.TBNZ
                or Arm64Opcode.BR or Arm64Opcode.BLR or Arm64Opcode.RET)
            {
                FlushAllMovChains();
                // BL/BLR = function call. Caller-saved registers (X0-X18)
                // are clobbered on return. Invalidate ADRP page tracking
                // so subsequent LDR_SIMD_IMM doesn't use stale page addresses.
                if (arm.Opcode is Arm64Opcode.BL or Arm64Opcode.BLR)
                {
                    for (int r = 0; r <= 18; r++)
                        _adrpPageValid[r] = false;
                }
            }
            else
            {
                // Flush source registers (Rn, Rm) if they have active chains
                FlushIfSourced(arm.Rn);
                if (arm.Rm < 31) FlushIfSourced(arm.Rm);
                
                // For STR/STP, Rd is actually a source (value being stored)
                if (arm.Opcode is Arm64Opcode.STR_IMM or Arm64Opcode.STR_REG
                    or Arm64Opcode.STRB_IMM or Arm64Opcode.STRH_IMM or Arm64Opcode.STP)
                {
                    FlushIfSourced(arm.Rd);
                }
                
                // For multiply-add instructions, arm.Shift encodes the Ra (accumulator) source
                if (arm.Opcode is Arm64Opcode.MADD or Arm64Opcode.MSUB 
                    or Arm64Opcode.FMADD or Arm64Opcode.FMSUB
                    or Arm64Opcode.FNMADD or Arm64Opcode.FNMSUB)
                {
                    FlushIfSourced(arm.Shift);
                }
                // If this instruction writes to a register that has an active chain, flush it
                if (WritesToRd(arm) && arm.Rd < 31 && _movActive[arm.Rd])
                    FlushMovChain(arm.Rd);
                // Invalidate ADRP page tracking when a register is overwritten by
                // any non-ADRP instruction. Without this, LDR_SIMD_IMM reads stale
                // page addresses for registers that have been repurposed (e.g., x19
                // reused as array pointer after initially holding an ADRP page addr).
                if (WritesToRd(arm) && arm.Rd < 31 &&
                    arm.Opcode != Arm64Opcode.ADRP)
                {
                    _adrpPageValid[arm.Rd] = false;
                }
                // LDP writes to BOTH Rd and Rm — flush Rm's chain too
                if (arm.Opcode is Arm64Opcode.LDP or Arm64Opcode.LDP_SIMD
                    && arm.Rm < 31 && _movActive[arm.Rm])
                    FlushMovChain(arm.Rm);
                // LDP also invalidates ADRP for Rm
                if (arm.Opcode is Arm64Opcode.LDP or Arm64Opcode.LDP_SIMD
                    && arm.Rm < 31)
                    _adrpPageValid[arm.Rm] = false;
            }
        }


        // ── Prologue/Epilogue: emit exactly ONE stack_alloc / stack_free ──
        // SUB SP, SP, #imm → the actual frame allocation → emit stack_alloc
        if ((arm.Opcode == Arm64Opcode.SUB_IMM) && arm.Rd == 31 && arm.Rn == 31)
        {
            Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.StackAlloc, CollapsedCount = 1 });
            return;
        }
        // ADD SP, SP, #imm → the actual frame deallocation → emit stack_free
        if ((arm.Opcode == Arm64Opcode.ADD_IMM) && arm.Rd == 31 && arm.Rn == 31)
        {
            Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.StackFree, CollapsedCount = 1 });
            return;
        }
        // STR X30, [SP, #offset] → LR save, suppress
        if (arm.Opcode == Arm64Opcode.STR_IMM && arm.Rd == 30 && arm.Rn == 31)
            return;
        // LDR X30, [SP, #offset] → LR restore, suppress
        if (arm.Opcode == Arm64Opcode.LDR_IMM && arm.Rd == 30 && arm.Rn == 31)
            return;
        // Pre-indexed STP/STR to SP with negative offset + writeback = combined stack alloc
        // (e.g. STP X29,X30,[SP,#-0x30]! or STR X30,[SP,#-0x10]!)
        //
        // OFFSET NORMALIZATION: The raw offsets are relative to the ORIGINAL SP
        // (e.g., -0x10 from old SP). All post-frame instructions use the NEW SP.
        // Normalize: new_offset = raw_offset - arm.Immediate (Immediate is negative,
        // so this shifts offsets into the post-frame reference).
        //   STP X30, X8, [SP, #-0x10]!  →  StackAlloc + store [SP+0], X30 + store [SP+8], X8
        if ((arm.Opcode == Arm64Opcode.STP || arm.Opcode == Arm64Opcode.STR_IMM) && arm.Rn == 31 && arm.Writeback != 0 && arm.Immediate < 0)
        {
            Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.StackAlloc, CollapsedCount = 1 });

            // Emit stores with normalized post-frame offsets
            // Formula: normalized = raw - adjustment, where adjustment = arm.Immediate (negative)
            long adjustment = arm.Immediate; // e.g., -0x10
            if (arm.Opcode == Arm64Opcode.STP)
            {
                byte w = (byte)(arm.Is64Bit ? 64 : 32);
                int step = arm.Is64Bit ? 8 : 4;
                Emit(IrInstruction.CreateStore(addr, Mem(31, arm.Immediate - adjustment, w), DataReg(arm.Rd, arm.Is64Bit)));
                Emit(IrInstruction.CreateStore(addr + 1, Mem(31, arm.Immediate + step - adjustment, w), DataReg(arm.Rm, arm.Is64Bit)));
            }
            else // STR_IMM
            {
                byte w = (byte)(arm.Is64Bit ? 64 : 32);
                Emit(IrInstruction.CreateStore(addr, Mem(31, arm.Immediate - adjustment, w), DataReg(arm.Rd, arm.Is64Bit)));
            }
            return;
        }
        // Post-indexed LDP/LDR from SP with writeback in epilogue = combined stack free
        if ((arm.Opcode == Arm64Opcode.LDP || arm.Opcode == Arm64Opcode.LDR_IMM) && arm.Rn == 31 && arm.Writeback != 0 && arm.Immediate > 0)
        {
            Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.StackFree, CollapsedCount = 1 });
            return;
        }

        switch (arm.Opcode)
        {
            case Arm64Opcode.NOP:
                Emit(IrInstruction.CreateNop(addr)); return;

            // ── Data Movement (MOVZ/MOVK/MOVN — deferred chain tracking) ──
            //
            // ARM64 compilers interleave MOVZ+MOVK chains across different registers
            // for pipeline scheduling. For example:
            //   MOVZ X9, #0x851F, LSL #0     ← X9 chain starts
            //   MOVK X9, #0x51EB, LSL #16    ← X9 part 2
            //   LDR  X0, [X8, #0xB8]         ← unrelated instruction
            //   MOVZ X8, #0xA, LSL #0         ← X8 chain starts
            //   MOVK X9, #0x1EB8, LSL #32    ← X9 part 3 (interleaved!)
            //   MOVK X8, #0x41A4, LSL #48    ← X8 completes
            //   MOVK X9, #0x4009, LSL #48    ← X9 completes
            //
            // We use per-register accumulators (_movAccum) to track partial chains.
            // Each MOVZ starts a new chain; each MOVK merges into the existing chain.
            // The chain is flushed (emitted) when:
            //   - All expected slots are filled (2 for 32-bit float, 4 for 64-bit double/long)
            //   - Another instruction writes to the same register
            //   - The register is read as a source by a non-MOVK instruction
            //   - End of method
            case Arm64Opcode.MOVZ:
            {
                int rd = arm.Rd;
                if (rd < 31)
                {
                    // If there's an existing chain on this register, flush it first
                    FlushMovChain(rd);

                    _movAccum[rd] = (ulong)(ushort)arm.Immediate << arm.Shift;
                    _movIs64[rd] = arm.Is64Bit;
                    _movActive[rd] = true;
                    _movSlotsFilled[rd] = 1;
                    _movAddr[rd] = addr;

                    // Check if this is a standalone MOVZ (no MOVK follows for this reg)
                    // by scanning a small window ahead
                    bool hasFollowingMovk = false;
                    for (int j = i + 1; j < armInsts.Length && j <= i + 8; j++)
                    {
                        if (armInsts[j].Opcode == Arm64Opcode.MOVK && armInsts[j].Rd == rd)
                        { hasFollowingMovk = true; break; }
                        // Stop scanning if something else writes to rd
                        if (WritesToRd(armInsts[j]) && armInsts[j].Rd == rd && armInsts[j].Opcode != Arm64Opcode.MOVK)
                            break;
                    }

                    if (!hasFollowingMovk)
                    {
                        // Standalone MOVZ — emit immediately
                        FlushMovChain(rd);
                    }
                }
                else
                {
                    // ZR register — just emit directly
                    long val = arm.Immediate << arm.Shift;
                    Emit(IrInstruction.CreateLoadImm(addr, Reg(rd, arm.Is64Bit),
                        IrOperand.Immediate(val, (byte)(arm.Is64Bit ? 64 : 32))));
                }
                return;
            }

            case Arm64Opcode.MOVK:
            {
                int rd = arm.Rd;
                if (rd < 31 && _movActive[rd])
                {
                    // Merge this MOVK into the active chain
                    ulong mask = ~(0xFFFFUL << arm.Shift);
                    _movAccum[rd] = (_movAccum[rd] & mask) | ((ulong)(ushort)arm.Immediate << arm.Shift);
                    _movIs64[rd] = _movIs64[rd] || arm.Is64Bit;
                    _movSlotsFilled[rd]++;

                    // Auto-flush when chain is complete:
                    //   32-bit: 2 slots (MOVZ + 1 MOVK)
                    //   64-bit: 4 slots (MOVZ + 3 MOVK)
                    int expectedSlots = _movIs64[rd] ? 4 : 2;
                    if (_movSlotsFilled[rd] >= expectedSlots)
                        FlushMovChain(rd);
                }
                else
                {
                    // Standalone MOVK without preceding MOVZ — emit as raw value
                    long movkVal = (long)((ulong)(ushort)arm.Immediate << arm.Shift);
                    Emit(IrInstruction.CreateLoadImm(addr, Reg(arm.Rd, arm.Is64Bit),
                        IrOperand.Immediate(movkVal, (byte)(arm.Is64Bit ? 64 : 32))));
                }
                return;
            }

            case Arm64Opcode.MOVN:
            {
                int rd = arm.Rd;
                if (rd < 31) FlushMovChain(rd); // kill any active chain
                ulong notVal = ~((ulong)(ushort)arm.Immediate << arm.Shift);
                if (!arm.Is64Bit) notVal &= 0xFFFFFFFF;
                Emit(IrInstruction.CreateLoadImm(addr, Reg(arm.Rd, arm.Is64Bit),
                    IrOperand.Immediate((long)notVal, (byte)(arm.Is64Bit ? 64 : 32))));
                return;
            }

            case Arm64Opcode.MOV_REG:
                Emit(IrInstruction.CreateAssign(addr, Reg(arm.Rd, arm.Is64Bit), DataReg(arm.Rn, arm.Is64Bit))); return;

            // ── Integer Arithmetic ──
            case Arm64Opcode.ADD_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Add, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Imm(arm.Immediate))); return;
            case Arm64Opcode.ADD_REG:
            {
                IrOperand src2;
                if (arm.Shift > 0)
                {
                    // Emit: tmp = Rm << shift; Rd = Rn + tmp
                    // We use intra-procedure scratch registers (X16/X17) as safe temporaries to avoid overwriting Rn
                    int tmpReg = (arm.Rn == 16) ? 17 : 16;
                    var shiftedReg = IrOperand.Register(tmpReg, arm.Is64Bit);
                    Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Shl, shiftedReg, DataReg(arm.Rm, arm.Is64Bit), Imm(arm.Shift)));
                    src2 = shiftedReg;
                    // Use addr+1 so SSA gives this a distinct address
                    Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), src2));
                }
                else
                {
                    Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Add, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), DataReg(arm.Rm, arm.Is64Bit)));
                }
                return;
            }

            case Arm64Opcode.SUB_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Sub, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Imm(arm.Immediate))); return;
            case Arm64Opcode.SUB_REG:
            {
                IrOperand src2;
                if (arm.Shift > 0)
                {
                    int tmpReg = (arm.Rn == 16) ? 17 : 16;
                    var shiftedReg = IrOperand.Register(tmpReg, arm.Is64Bit);
                    Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Shl, shiftedReg, DataReg(arm.Rm, arm.Is64Bit), Imm(arm.Shift)));
                    src2 = shiftedReg;
                    Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Sub, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), src2));
                }
                else
                {
                    Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Sub, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), DataReg(arm.Rm, arm.Is64Bit)));
                }
                return;
            }

            case Arm64Opcode.MADD:
            {
                if (arm.Shift == 31)
                { Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Mul, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit))); return; }
                int tmpReg = (arm.Rn == 16 || arm.Rm == 16 || arm.Shift == 16) ? 17 : 16;
                var mulTmp = IrOperand.Register(tmpReg, arm.Is64Bit);
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Mul, mulTmp, Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit)));
                Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Shift, arm.Is64Bit), mulTmp));
                return;
            }

            case Arm64Opcode.SDIV:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.SDiv, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit))); return;
            case Arm64Opcode.UDIV:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.UDiv, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit))); return;

            case Arm64Opcode.SMULL:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.SMulWide, Reg(arm.Rd, true), Reg(arm.Rn, false), Reg(arm.Rm, false))); return;

            // ── Bitwise ──
            case Arm64Opcode.AND_IMM:
                // AND Rd, Rn, #imm — register 31 is WZR (zero), not SP
                if (arm.Rn == 31)
                { Emit(IrInstruction.CreateLoadImm(addr, Reg(arm.Rd, arm.Is64Bit), Imm(0))); return; } // AND x, 0, imm → 0
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.And, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Imm(arm.Immediate))); return;
            case Arm64Opcode.AND_REG:
            case Arm64Opcode.BIC_REG:
            case Arm64Opcode.ANDS_REG:
            case Arm64Opcode.BICS_REG:
                if (arm.Rn == arm.Rm)
                {
                    // A & A is just A. This is frequently used for 32-bit zero extension.
                    Emit(IrInstruction.CreateAssign(addr, Reg(arm.Rd, arm.Is64Bit), DataReg(arm.Rn, arm.Is64Bit)));
                    return;
                }
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.And, Reg(arm.Rd, arm.Is64Bit), DataReg(arm.Rn, arm.Is64Bit), DataReg(arm.Rm, arm.Is64Bit))); return;

            case Arm64Opcode.ORR_IMM:
                // ORR Rd, Rn, #imm — when Rn==31 (WZR), this is MOV Rd, #imm
                if (arm.Rn == 31)
                { Emit(IrInstruction.CreateLoadImm(addr, Reg(arm.Rd, arm.Is64Bit), Imm(arm.Immediate))); return; }
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Or, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Imm(arm.Immediate))); return;
            case Arm64Opcode.ORR_REG:
            case Arm64Opcode.ORN_REG:
                // ORR Rd, Rn, Rm — register operands use WZR for reg31
                if (arm.Rn == 31)
                { Emit(IrInstruction.CreateLoadImm(addr, Reg(arm.Rd, arm.Is64Bit), DataReg(arm.Rm, arm.Is64Bit))); return; }
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Or, Reg(arm.Rd, arm.Is64Bit), DataReg(arm.Rn, arm.Is64Bit), DataReg(arm.Rm, arm.Is64Bit))); return;

            case Arm64Opcode.EOR_IMM:
                // EOR Rd, Rn, #imm — when Rn==31 (WZR), this is MOV Rd, #imm (XOR 0, imm = imm)
                if (arm.Rn == 31)
                { Emit(IrInstruction.CreateLoadImm(addr, Reg(arm.Rd, arm.Is64Bit), Imm(arm.Immediate))); return; }
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Xor, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Imm(arm.Immediate))); return;
            case Arm64Opcode.EOR_REG:
            case Arm64Opcode.EON_REG:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Xor, Reg(arm.Rd, arm.Is64Bit), DataReg(arm.Rn, arm.Is64Bit), DataReg(arm.Rm, arm.Is64Bit))); return;

            // Variable shifts (register-specified shift amount)
            case Arm64Opcode.LSLV:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Shl, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit))); return;
            case Arm64Opcode.LSRV:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Shr, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit))); return;
            case Arm64Opcode.ASRV:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Sar, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit))); return;

            // ANDS_IMM — TST alias when Rd==31
            case Arm64Opcode.ANDS_IMM:
                if (arm.Rd == 31)
                { Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Test, Sources = [Reg(arm.Rn, arm.Is64Bit), Imm(arm.Immediate)] }); return; }
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.And, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Imm(arm.Immediate))); return;

            // UBFM / SBFM — bitfield extract
            // Decoder packs: immr → arm.Rm, imms → arm.Shift
            case Arm64Opcode.UBFM:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.BitfieldExtractUnsigned, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [Reg(arm.Rn, arm.Is64Bit), Imm(arm.Rm), Imm(arm.Shift)] }); return;
            case Arm64Opcode.SBFM:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.BitfieldExtractSigned, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [Reg(arm.Rn, arm.Is64Bit), Imm(arm.Rm), Imm(arm.Shift)] }); return;

            // ── Memory ──
            case Arm64Opcode.LDR_IMM:
            {
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(IrInstruction.CreateLoad(addr, Reg(arm.Rd, arm.Is64Bit), Mem(arm.Rn, offset, (byte)(arm.Is64Bit ? 64 : 32))));
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }
            case Arm64Opcode.STR_IMM:
            {
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(IrInstruction.CreateStore(addr, Mem(arm.Rn, offset, (byte)(arm.Is64Bit ? 64 : 32)), DataReg(arm.Rd, arm.Is64Bit)));
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }
            case Arm64Opcode.LDRB_IMM:
            {
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(IrInstruction.CreateLoad(addr, Reg(arm.Rd, false), Mem(arm.Rn, offset, 8)));
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }
            case Arm64Opcode.STRB_IMM:
            {
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(IrInstruction.CreateStore(addr, Mem(arm.Rn, offset, 8), DataReg(arm.Rd, false)));
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }
            case Arm64Opcode.LDRH_IMM:
            {
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(IrInstruction.CreateLoad(addr, Reg(arm.Rd, false), Mem(arm.Rn, offset, 16)));
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }
            case Arm64Opcode.STRH_IMM:
            {
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(IrInstruction.CreateStore(addr, Mem(arm.Rn, offset, 16), DataReg(arm.Rd, false)));
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }
            case Arm64Opcode.LDR_REG:
            {
                string rmStr = arm.OffsetIs64Bit ? $"X{arm.Rm}" : $"W{arm.Rm}";
                Emit(new IrInstruction { 
                    Address = addr, 
                    Opcode = IrOpcode.Load, 
                    Destination = Reg(arm.Rd, arm.Is64Bit), 
                    Sources = [IrOperand.AnnotatedMemory(arm.Rn, 0, (byte)(arm.Is64Bit ? 64 : 32), $"[{RegStr(arm.Rn)} + {rmStr}]"), Reg(arm.Rm, arm.OffsetIs64Bit)] 
                }); 
                return;
            }
            case Arm64Opcode.STR_REG:
            {
                string rmStr = arm.OffsetIs64Bit ? $"X{arm.Rm}" : $"W{arm.Rm}";
                Emit(new IrInstruction { 
                    Address = addr, 
                    Opcode = IrOpcode.Store, 
                    Destination = null, 
                    Sources = [IrOperand.AnnotatedMemory(arm.Rn, 0, (byte)(arm.Is64Bit ? 64 : 32), $"[{RegStr(arm.Rn)} + {rmStr}]"), DataReg(arm.Rd, arm.Is64Bit), Reg(arm.Rm, arm.OffsetIs64Bit)] 
                }); 
                return;
            }

            // LDRSW — sign-extending word load (always 64-bit dest)
            case Arm64Opcode.LDRSW_IMM:
            {
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Load, Destination = Reg(arm.Rd, true), Sources = [Mem(arm.Rn, offset, 32)], Annotation = "sign-extend 32→64" });
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }

            // ── Comparison ──
            case Arm64Opcode.CMP_IMM:
            case Arm64Opcode.CMP_REG:
            case Arm64Opcode.SUBS_IMM:
            case Arm64Opcode.SUBS_REG:
                var cmpSrc = (arm.Opcode == Arm64Opcode.CMP_IMM || arm.Opcode == Arm64Opcode.SUBS_IMM) ? Imm(arm.Immediate) : DataReg(arm.Rm, arm.Is64Bit);
                if (arm.Rd == 31)
                { Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Compare, Sources = [Reg(arm.Rn, arm.Is64Bit), cmpSrc] }); return; }
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Sub, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [Reg(arm.Rn, arm.Is64Bit), cmpSrc], Annotation = "sets flags" }); return;

            // CCMP variants
            case Arm64Opcode.CCMP_IMM:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Compare, Sources = [IrOperand.Condition(arm.Condition), Reg(arm.Rn, arm.Is64Bit), Imm(arm.Rm)], Annotation = "CCMP" }); return;

            // ── Conditional Select ──
            case Arm64Opcode.CSEL:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Select, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [IrOperand.Condition(arm.Condition), DataReg(arm.Rn, arm.Is64Bit), DataReg(arm.Rm, arm.Is64Bit)] }); return;

            case Arm64Opcode.CSINC:
                if (arm.Rn == 31 && arm.Rm == 31) // CSET alias
                { Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.SelectInc, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [IrOperand.Condition((byte)(arm.Condition ^ 1)), IrOperand.Zero(arm.Is64Bit), IrOperand.Zero(arm.Is64Bit)], Annotation = "CSET" }); return; }
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.SelectInc, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [IrOperand.Condition(arm.Condition), DataReg(arm.Rn, arm.Is64Bit), DataReg(arm.Rm, arm.Is64Bit)] }); return;

            case Arm64Opcode.CSINV:
                if (arm.Rn == 31 && arm.Rm == 31) // CSETM alias
                { Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.SelectInv, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [IrOperand.Condition((byte)(arm.Condition ^ 1)), IrOperand.Zero(arm.Is64Bit), IrOperand.Zero(arm.Is64Bit)], Annotation = "CSETM" }); return; }
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.SelectInv, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [IrOperand.Condition(arm.Condition), DataReg(arm.Rn, arm.Is64Bit), DataReg(arm.Rm, arm.Is64Bit)] }); return;

            // ── Branches ──
            case Arm64Opcode.B:
            {
                var bResolved = _callResolver.TryResolve((ulong)arm.Immediate);

                // Tail call: B to a known method (compiler optimized call+ret → branch)
                if (bResolved != null && !bResolved.IsRuntimeHelper && bResolved.DeclaringType != null)
                {
                    string bAnnotation = bResolved.FullName;
                    if (bResolved.IsStatic) bAnnotation = "static " + bAnnotation;
                    if (bResolved.MethodIndex >= 0) bAnnotation = $"[M:{bResolved.MethodIndex}] " + bAnnotation;
                    var bCallTarget = IrOperand.CallTarget((ulong)arm.Immediate, bAnnotation);

                    int bGpParams = bResolved.ParameterCount - bResolved.FpParamCount;
                    int bGpUserArgs = bGpParams + (bResolved.IsStatic ? 0 : 1);
                    int bMethodInfoIdx = bGpUserArgs; // x-register index for MethodInfo*
                    if (bGpUserArgs > 8) bGpUserArgs = 8;
                    int bFpArgs = Math.Min(bResolved.FpArgCount, 8);
                    bool bHasMethodInfo = bMethodInfoIdx >= 0 && bMethodInfoIdx < 8;

                    int bTotal = 1 + bGpUserArgs + bFpArgs + (bHasMethodInfo ? 1 : 0);
                    var bCallSources = new IrOperand[bTotal];
                    bCallSources[0] = bCallTarget;
                    for (int a = 0; a < bGpUserArgs; a++)
                        bCallSources[1 + a] = Reg(a, true);
                    for (int f = 0; f < bFpArgs; f++)
                        bCallSources[1 + bGpUserArgs + f] = FpReg(f, bResolved.HasDoubleArgs);
                    if (bHasMethodInfo)
                        bCallSources[bTotal - 1] = Reg(bMethodInfoIdx, true);

                    Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.TailCall, Destination = null, Sources = bCallSources, Annotation = bAnnotation });
                    return;
                }

                // Check for Clang out-of-line veneer: LDR Rx,[Ry,#off] + B back
                // These are 2-instruction code fragments the compiler splits out for
                // metadata page loads. We inline the LDR and skip the branch.
                if (_elf != null && _binaryBytes != null)
                {
                    ulong veneerVA = (ulong)arm.Immediate;
                    long veneerOff = _elf.VirtualToFileOffset(veneerVA);
                    if (veneerOff >= 0 && veneerOff + 8 <= _binaryBytes.Length)
                    {
                        uint word0 = BitConverter.ToUInt32(_binaryBytes, (int)veneerOff);
                        uint word1 = BitConverter.ToUInt32(_binaryBytes, (int)veneerOff + 4);
                        
                        // Decode LDR (unsigned offset, 64-bit): 11111001 01 [imm12] [Rn] [Rt]
                        bool isLdr64 = (word0 & 0xFFC00000) == 0xF9400000;
                        // Decode B (unconditional): 000101 [imm26]
                        bool isB = (word1 & 0xFC000000) == 0x14000000;
                        
                        if (isLdr64 && isB)
                        {
                            int vRt = (int)(word0 & 0x1F);
                            int vRn = (int)((word0 >> 5) & 0x1F);
                            int vImm12 = (int)((word0 >> 10) & 0xFFF);
                            long vOffset = vImm12 * 8; // scale by 8 for 64-bit LDR
                            
                            // Inline the veneer's LDR as an IR Load instruction
                            Emit(IrInstruction.CreateLoad(addr, Reg(vRt, true),
                                IrOperand.Memory(vRn, vOffset, 64)));
                            return;
                        }
                    }
                }

                // Regular branch (intra-function jump)
                var brInst = IrInstruction.CreateBranch(addr, IrOperand.Label((ulong)arm.Immediate));
                if (bResolved?.FullName != null) brInst.Annotation = bResolved.FullName;
                Emit(brInst);
                return;
            }
            case Arm64Opcode.B_COND:
                Emit(IrInstruction.CreateCondBranch(addr, IrOperand.Condition(arm.Condition), IrOperand.Label((ulong)arm.Immediate))); return;
            // CBZ — Compare and Branch if Zero: branch taken when register IS zero (null)
            // IR semantic: "if (!reg) goto label" — the taken path is the null/zero case
            case Arm64Opcode.CBZ:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.ConditionalBranch, Condition = IrBranchCondition.Zero, Sources = [Reg(arm.Rd, arm.Is64Bit), IrOperand.Label((ulong)arm.Immediate)], Annotation = "cbz" }); return;
            // CBNZ — Compare and Branch if Not Zero: branch taken when register is NOT zero
            // IR semantic: "if (reg) goto label" — the taken path is the non-null/nonzero case
            case Arm64Opcode.CBNZ:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.ConditionalBranch, Condition = IrBranchCondition.NotZero, Sources = [Reg(arm.Rd, arm.Is64Bit), IrOperand.Label((ulong)arm.Immediate)], Annotation = "cbnz" }); return;

            // TBZ / TBNZ — test bit and branch
            case Arm64Opcode.TBZ:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Test, Sources = [Reg(arm.Rd, arm.Is64Bit), Imm(1L << arm.Shift)] });
                Emit(new IrInstruction { Address = addr + 1, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0x0), IrOperand.Label((ulong)arm.Immediate)] });
                return;
            case Arm64Opcode.TBNZ:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Test, Sources = [Reg(arm.Rd, arm.Is64Bit), Imm(1L << arm.Shift)] });
                Emit(new IrInstruction { Address = addr + 1, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0x1), IrOperand.Label((ulong)arm.Immediate)] });
                return;

            // BL — direct call
            case Arm64Opcode.BL:
                var resolved = _callResolver.TryResolve((ulong)arm.Immediate);
                string? annotation = resolved?.FullName;
                if (resolved != null && resolved.IsStatic && annotation != null)
                    annotation = "static " + annotation;
                if (resolved != null && resolved.MethodIndex >= 0 && annotation != null)
                    annotation = $"[M:{resolved.MethodIndex}] " + annotation;
                var callTarget = IrOperand.CallTarget((ulong)arm.Immediate, annotation ?? $"func_{(ulong)arm.Immediate:X}");

                // Build sources: [target, arg0, arg1, ..., argN]
                // ARM64 calling convention: x0-x7 for GP args, s0-s7 for FP args
                // Instance methods: x0 = this, x1-x7 = user args
                // Static methods: x0-x7 = user args
                // +1 for the hidden MethodInfo* trailing arg (always passed in last GP reg)
                // ARM64 AAPCS: float/double params use s0-s7/d0-d7, NOT x0-x7.
                //
                // Source ordering: [target, GP_user_args, FP_args, MethodInfo*]
                // MethodInfo* MUST be last so ExprPropagator can strip it with lastArgSource--.
                int gpUserArgs = 0;    // GP args excluding MethodInfo*
                int methodInfoGpIdx = -1; // which x-register holds MethodInfo*
                int fpArgCount = 0;
                bool fpArgsAreDouble = false;
                if (resolved != null && !resolved.IsRuntimeHelper)
                {
                    int gpParams = resolved.ParameterCount - resolved.FpParamCount; // non-FP user params
                    gpUserArgs = gpParams + (resolved.IsStatic ? 0 : 1); // +1 for 'this' on instance
                    methodInfoGpIdx = gpUserArgs; // MethodInfo* occupies the next x-register
                    if (gpUserArgs > 8) gpUserArgs = 8;

                    fpArgCount = resolved.FpArgCount;
                    fpArgsAreDouble = resolved.HasDoubleArgs;
                    
                    // P3 FIX: Infer FP args for generic methods (e.g. Nullable<float>::.ctor) where FpArgCount=0
                    if (fpArgCount == 0 && resolved.FullName != null && 
                        (resolved.FullName.Contains("<System.Single>") || resolved.FullName.Contains("<System.Double>") ||
                         resolved.FullName.Contains("<float>") || resolved.FullName.Contains("<double>")))
                    {
                        fpArgCount = 1; // Heuristic: typical generic value types take 1 FP arg
                        fpArgsAreDouble = resolved.FullName.Contains("Double") || resolved.FullName.Contains("double");
                        if (gpUserArgs > (resolved.IsStatic ? 0 : 1)) gpUserArgs--;
                        methodInfoGpIdx = gpUserArgs;
                    }
                    
                    if (fpArgCount > 8) fpArgCount = 8;
                }

                // For C math library functions, override FP argument count and precision
                if (resolved != null && resolved.IsRuntimeHelper && resolved.MethodName != null)
                {
                    fpArgCount = GetMathFpArgCount(resolved.MethodName);
                    fpArgsAreDouble = IsMathDoubleVariant(resolved.MethodName);
                }

                bool hasMethodInfo = methodInfoGpIdx >= 0 && methodInfoGpIdx < 8;
                int totalSources = 1 + gpUserArgs + fpArgCount + (hasMethodInfo ? 1 : 0);
                var callSources = new IrOperand[totalSources];
                callSources[0] = callTarget;
                // GP user args (x0..xN, excluding MethodInfo*)
                for (int a = 0; a < gpUserArgs; a++)
                    callSources[1 + a] = Reg(a, true);
                // FP args (s0/d0, s1/d1, ...)
                for (int f = 0; f < fpArgCount; f++)
                    callSources[1 + gpUserArgs + f] = FpReg(f, fpArgsAreDouble);
                // MethodInfo* — always last
                if (hasMethodInfo)
                    callSources[totalSources - 1] = Reg(methodInfoGpIdx, true);

                // Resolve return type from metadata
                string? resultType = null;
                if (resolved != null && !resolved.IsVoid && !resolved.IsRuntimeHelper && resolved.MethodIndex >= 0)
                    resultType = _callResolver.ResolveReturnType(resolved.MethodIndex);

                // Remap C math function names to C# Mathf/Math equivalents in the annotation.
                // Use "." not "::" — ExprPropagator treats "::" as a managed call and strips
                // the last source as MethodInfo*, which would eat our FP argument.
                if (resolved != null && resolved.IsRuntimeHelper && resolved.MethodName != null)
                {
                    string? csharpName = MapCMathToCSharp(resolved.MethodName);
                    if (csharpName != null)
                    {
                        annotation = $"static {csharpName}";
                        resultType ??= fpArgsAreDouble ? "System.Double" : "System.Single";
                    }
                }

                // Void and HFA returns: no scalar destination (HFAs return in multiple FPU registers)
                // Float/double return: s0/d0 (FP register) per ARM64 AAPCS.
                // Everything else: x0 (GP register).
                // For resolved managed void methods, callDst is null — no return value.
                // For runtime helpers and unresolved calls, we still assign x0 because
                // PLT stubs (e.g., __cxa_begin_catch) and metadata may lie about void.
                IrOperand? callDst;
                if (resolved != null && resolved.IsVoid && !resolved.IsRuntimeHelper)
                    callDst = null;
                else if (resolved != null && resolved.ReturnHfaSize >= 2)
                    callDst = null;
                else if (IsFloatReturnType(resultType, resolved?.MethodName))
                    callDst = FpReg(0, resultType == "System.Double" || resultType == "double");
                else
                    callDst = Reg(0, true);

                bool isCtor = resolved != null && resolved.MethodName != null && (resolved.MethodName.EndsWith("::.ctor") || resolved.MethodName == ".ctor");
                var clobberSet = isCtor 
                    ? IrInstruction.Arm64CallerSavedRegistersNoReturn 
                    : IrInstruction.Arm64CallerSavedRegisters;

                Emit(new IrInstruction { 
                    Address = addr, 
                    Opcode = IrOpcode.Call, 
                    Destination = callDst, 
                    Sources = callSources, 
                    Annotation = annotation, 
                    ResultType = resultType, 
                    ClobberedRegisters = clobberSet,
                    TargetMethodIndex = resolved?.MethodIndex >= 0 ? resolved.MethodIndex : null,
                    IsNoReturn = IrInstruction.IsKnownNoReturn(annotation)
                });

                return;

            // BLR — indirect call via register
            case Arm64Opcode.BLR:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.IndirectCall, Destination = Reg(0, true), Sources = [Reg(arm.Rn, true)], ClobberedRegisters = IrInstruction.Arm64CallerSavedRegisters }); return;

            // BR — indirect branch via register
            case Arm64Opcode.BR:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.IndirectBranch, Sources = [Reg(arm.Rn, true)] }); return;

            case Arm64Opcode.RET:
            {
                // For non-void methods, include the return register as a source
                // so SSA data flow captures the return value.
                // ARM64 ABI: GP return in x0/w0, FP return in s0/d0.
                IrOperand? retVal = null;
                if (_currentReturnType != "void")
                {
                    bool isFpReturn = IsFloatReturnType(_currentReturnType, null);
                    retVal = isFpReturn ? FpReg(0, _currentReturnType == "double") : Reg(0, true);
                }
                Emit(IrInstruction.CreateReturn(addr, retVal)); return;
            }

            // ── ADRP collapse ──
            case Arm64Opcode.ADRP:
                if (i + 1 < armInsts.Length && armInsts[i+1].Opcode == Arm64Opcode.ADD_IMM && armInsts[i+1].Rn == arm.Rd)
                {
                    var add = armInsts[i+1]; i++;
                    ulong target = (ulong)arm.Immediate + (ulong)add.Immediate;
                    int destRd = add.Rd;
                    
                    // Bug 7: Flush ADD destination if it has an active chain
                    if (destRd < 31 && _movActive[destRd])
                        FlushMovChain(destRd);

                    if (arm.Rd != destRd)
                    {
                        // Emit both if destinations differ to avoid losing arm.Rd state
                        if (arm.Rd < 31) { _adrpPageAddr[arm.Rd] = arm.Immediate; _adrpPageValid[arm.Rd] = true; }
                        Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.LoadAddress, Destination = Reg(arm.Rd, true), Sources = [IrOperand.Label((ulong)arm.Immediate)] });
                        
                        if (destRd < 31) { _adrpPageAddr[destRd] = (long)target; _adrpPageValid[destRd] = true; }
                        Emit(new IrInstruction { Address = addr + 4, Opcode = IrOpcode.LoadAddress, Destination = Reg(destRd, true), Sources = [IrOperand.Label(target)] });
                    }
                    else
                    {
                        // Standard collapse
                        if (destRd < 31) { _adrpPageAddr[destRd] = (long)target; _adrpPageValid[destRd] = true; }
                        Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.LoadAddress, Destination = Reg(destRd, true), Sources = [IrOperand.Label(target)], CollapsedCount = 2 });
                    }
                    return;
                }
                if (arm.Rd < 31) { _adrpPageAddr[arm.Rd] = arm.Immediate; _adrpPageValid[arm.Rd] = true; }
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.LoadAddress, Destination = Reg(arm.Rd, true), Sources = [IrOperand.Label((ulong)arm.Immediate)] }); return;

            // ── GP Pair load-store ──
            // STP/LDP to SP with positive offset = callee-saved register save/restore → normal stores/loads
            case Arm64Opcode.LDP:
            {
                byte w = (byte)(arm.Is64Bit ? 64 : 32); int step = arm.Is64Bit ? 8 : 4;
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                if (arm.Rd == arm.Rn)
                {
                    // Hardware LDP is parallel. If we load Rd first when Rd == Rn, we destroy the base pointer for Rm!
                    // Fix: Load Rm first, then Rd.
                    Emit(IrInstruction.CreateLoad(addr, Reg(arm.Rm, arm.Is64Bit), Mem(arm.Rn, offset + step, w)));
                    Emit(IrInstruction.CreateLoad(addr + 1, Reg(arm.Rd, arm.Is64Bit), Mem(arm.Rn, offset, w)));
                }
                else
                {
                    Emit(IrInstruction.CreateLoad(addr, Reg(arm.Rd, arm.Is64Bit), Mem(arm.Rn, offset, w)));
                    Emit(IrInstruction.CreateLoad(addr + 1, Reg(arm.Rm, arm.Is64Bit), Mem(arm.Rn, offset + step, w)));
                }
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 2, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }
            case Arm64Opcode.STP:
            {
                byte w = (byte)(arm.Is64Bit ? 64 : 32); int step = arm.Is64Bit ? 8 : 4;
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(IrInstruction.CreateStore(addr, Mem(arm.Rn, offset, w), DataReg(arm.Rd, arm.Is64Bit)));
                Emit(IrInstruction.CreateStore(addr + 1, Mem(arm.Rn, offset + step, w), DataReg(arm.Rm, arm.Is64Bit)));
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 2, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }

            // ── SIMD/FP Load/Store ──
            case Arm64Opcode.LDR_SIMD_IMM:
            {
                byte sw = SimdWidth(arm);
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                if (arm.Rn < 31 && _adrpPageValid[arm.Rn] && _elf != null && _binaryBytes != null)
                {
                    ulong memAddr = (ulong)(_adrpPageAddr[arm.Rn] + offset);
                    long fileOffset = _elf.VirtualToFileOffset(memAddr);
                    int byteCount = sw / 8;
                    if (fileOffset >= 0 && fileOffset + byteCount <= _binaryBytes.Length)
                    {
                        int fo = (int)fileOffset;
                        if (sw == 128)
                        {
                            // Q-register: 128-bit SIMD value
                            long rawLo = BitConverter.ToInt64(_binaryBytes, fo);
                            long rawHi = BitConverter.ToInt64(_binaryBytes, fo + 8);
                            var inst = IrInstruction.CreateLoadImm(addr, IrOperand.FpRegister(arm.Rd, sw), IrOperand.SimdImmediate(rawLo, rawHi));
                            Emit(inst);
                        }
                        else
                        {
                            long rawBits = 0;
                            if (sw == 64) rawBits = BitConverter.ToInt64(_binaryBytes, fo);
                            else if (sw == 32) rawBits = (long)(uint)BitConverter.ToInt32(_binaryBytes, fo);
                            else if (sw == 16) rawBits = (long)(ushort)BitConverter.ToInt16(_binaryBytes, fo);
                            else if (sw == 8) rawBits = _binaryBytes[fo];
                            Emit(IrInstruction.CreateLoadImm(addr, IrOperand.FpRegister(arm.Rd, sw), IrOperand.FloatImmediate(rawBits, sw)));
                        }
                        if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                        return;
                    }
                }
                Emit(IrInstruction.CreateLoad(addr, IrOperand.FpRegister(arm.Rd, sw), Mem(arm.Rn, offset, sw)));
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }
            case Arm64Opcode.STR_SIMD_IMM:
            {
                byte sw = SimdWidth(arm);
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(IrInstruction.CreateStore(addr, Mem(arm.Rn, offset, sw), IrOperand.FpRegister(arm.Rd, sw)));
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }
            case Arm64Opcode.LDP_SIMD:
            { 
                byte sw = SimdWidth(arm); int step = sw / 8;
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(IrInstruction.CreateLoad(addr, IrOperand.FpRegister(arm.Rd, sw), Mem(arm.Rn, offset, sw)));
                Emit(IrInstruction.CreateLoad(addr + 1, IrOperand.FpRegister(arm.Rm, sw), Mem(arm.Rn, offset + step, sw))); 
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 2, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return; 
            }
            case Arm64Opcode.STP_SIMD:
            { 
                byte sw = SimdWidth(arm); int step = sw / 8;
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(IrInstruction.CreateStore(addr, Mem(arm.Rn, offset, sw), IrOperand.FpRegister(arm.Rd, sw)));
                Emit(IrInstruction.CreateStore(addr + 1, Mem(arm.Rn, offset + step, sw), IrOperand.FpRegister(arm.Rm, sw))); 
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 2, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return; 
            }

            // SIMD/FP register-offset load/store (e.g., LDR S8, [X19, X8])
            // Used for float/double array element access at computed indices.
            case Arm64Opcode.LDR_SIMD_REG:
            {
                byte sw = SimdWidth(arm);
                Emit(IrInstruction.CreateLoadIndexed(addr, IrOperand.FpRegister(arm.Rd, sw),
                    IrOperand.Memory(arm.Rn, 0, sw),
                    IrOperand.Register(arm.Rm, arm.OffsetIs64Bit)));
                return;
            }
            case Arm64Opcode.STR_SIMD_REG:
            {
                byte sw = SimdWidth(arm);
                string rmStr = arm.OffsetIs64Bit ? $"X{arm.Rm}" : $"W{arm.Rm}";
                Emit(IrInstruction.CreateStore(addr,
                    IrOperand.AnnotatedMemory(arm.Rn, 0, sw, $"[{RegStr(arm.Rn)} + {rmStr}]"),
                    IrOperand.FpRegister(arm.Rd, sw)));
                return;
            }

            // MOVI zero
            case Arm64Opcode.MOVI_ZERO:
                Emit(IrInstruction.CreateLoadImm(addr, FpReg(arm.Rd, true), IrOperand.SimdImmediate(0, 0))); return;

            // ── SIMD ops ──
            case Arm64Opcode.SIMD_VECTOR_OP:
            {
                string mnemonic = arm.ToString();
                
                // Decode shape from raw bits
                int Q = (int)((arm.RawValue >> 30) & 1);
                int size = (int)((arm.RawValue >> 22) & 3);
                bool isFpOp = mnemonic.StartsWith("F") || mnemonic.Contains("FMOV");
                
                byte elementWidth;
                byte elementCount;
                if (isFpOp)
                {
                    int sz = size & 1; // bit 22 is sz for FP
                    elementWidth = (byte)(sz == 0 ? 32 : 64);
                    elementCount = (byte)((Q == 0 ? 64 : 128) / elementWidth);
                }
                else
                {
                    elementWidth = (byte)(8 << size);
                    elementCount = (byte)((Q == 0 ? 64 : 128) / elementWidth);
                }
                
                IrOperand destReg = IrOperand.VectorRegister(arm.Rd, elementWidth, elementCount);

                // FMOV V0.2S, #imm8(N) — vector float immediate, NOT a register move
                if (mnemonic.Contains("FMOV") && mnemonic.Contains("#imm8("))
                {
                    int start = mnemonic.IndexOf("#imm8(") + 6;
                    int end = mnemonic.IndexOf(')', start);
                    if (end > start && byte.TryParse(mnemonic[start..end], out byte imm8))
                    {
                        bool isDouble = elementWidth == 64;
                        long rawBits = Arm64Instruction.DecodeFpImm8RawBits(imm8, isDouble);
                        
                        if (elementCount > 1)
                        {
                            long rawLo, rawHi;
                            if (isDouble)
                            {
                                rawLo = rawBits;
                                rawHi = elementCount > 1 ? rawBits : 0;
                            }
                            else
                            {
                                rawLo = (rawBits << 32) | rawBits;
                                rawHi = elementCount > 2 ? rawLo : 0;
                            }
                            Emit(IrInstruction.CreateLoadImm(addr, destReg,
                                IrOperand.SimdImmediate(rawLo, rawHi)));
                        }
                        else
                        {
                            Emit(IrInstruction.CreateLoadImm(addr, destReg,
                                IrOperand.FloatImmediate(rawBits, elementWidth)));
                        }
                        return;
                    }
                }

                // Decode common SIMD three-same ops from mnemonic
                IrOpcode? simdOp = null;
                if (mnemonic.StartsWith("FMUL ")) simdOp = IrOpcode.FMul;
                else if (mnemonic.StartsWith("FADD ")) simdOp = IrOpcode.FAdd;
                else if (mnemonic.StartsWith("FSUB ")) simdOp = IrOpcode.FSub;
                else if (mnemonic.StartsWith("FDIV ")) simdOp = IrOpcode.FDiv;
                else if (mnemonic.StartsWith("FMLA ")) simdOp = IrOpcode.FAdd;
                else if (mnemonic.StartsWith("FMLS ")) simdOp = IrOpcode.FSub;
                else if (mnemonic.StartsWith("ADD ")) { simdOp = IrOpcode.Add; isFpOp = false; }
                else if (mnemonic.StartsWith("SUB ")) { simdOp = IrOpcode.Sub; isFpOp = false; }
                else if (mnemonic.StartsWith("MUL ")) { simdOp = IrOpcode.Mul; isFpOp = false; }

                IrOperand rnReg = IrOperand.VectorRegister(arm.Rn, elementWidth, elementCount);
                IrOperand rmReg = IrOperand.VectorRegister(arm.Rm, elementWidth, elementCount);

                if (simdOp.HasValue)
                {
                    Emit(new IrInstruction { Address = addr, Opcode = simdOp.Value,
                        Destination = destReg,
                        Sources = [rnReg, rmReg] });
                    return;
                }

                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Assign, Destination = destReg, Sources = [rnReg] });
                return;
            }
            case Arm64Opcode.SIMD_DUP:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Assign, Destination = FpReg(arm.Rd, true), Sources = [FpReg(arm.Rn, true)], Annotation = "DUP" }); return;
            case Arm64Opcode.SIMD_DUP_GP:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Assign, Destination = FpReg(arm.Rd, true), Sources = [Reg(arm.Rn, arm.Is64Bit)], Annotation = "DUP" }); return;
            case Arm64Opcode.SIMD_DUP_ELEMENT:
            {
                // imm5 encodes element size (lowest set bit) and index (bits above)
                // ARM DDI: lowest set bit → size: bit0=B(8), bit1=H(16), bit2=S(32), bit3=D(64)
                // index = bits above lowest set bit
                byte imm5 = arm.Shift;
                int lsb = imm5 & (-imm5); // isolate lowest set bit
                bool destIs64 = lsb == 8;  // D-register only when bit3 is THE lowest set bit
                int elemIdx = lsb switch
                {
                    1 => imm5 >> 1,  // byte
                    2 => imm5 >> 2,  // half
                    4 => imm5 >> 3,  // single
                    8 => imm5 >> 4,  // double
                    _ => 0
                };
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Assign,
                    Destination = FpReg(arm.Rd, destIs64),
                    Sources = [FpReg(arm.Rn, true)],
                    Annotation = $"MOV element[{elemIdx}]" }); return;
            }
            case Arm64Opcode.SIMD_INS:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Assign, Destination = FpReg(arm.Rd, true), Sources = [Reg(arm.Rn, arm.Is64Bit)] }); return;
            case Arm64Opcode.SIMD_UMOV:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Assign, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, true)], Annotation = "UMOV" }); return;
            case Arm64Opcode.SIMD_FADDP:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FAdd, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit)], Annotation = "FADDP" }); return;
            case Arm64Opcode.SIMD_SHL:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Shl, Destination = FpReg(arm.Rd, true), Sources = [FpReg(arm.Rn, true), Imm(arm.Immediate)], Annotation = "SIMD SHL" }); return;
            case Arm64Opcode.SIMD_SSHR:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Sar, Destination = FpReg(arm.Rd, true), Sources = [FpReg(arm.Rn, true), Imm(arm.Immediate)], Annotation = "SIMD SSHR" }); return;
            case Arm64Opcode.SIMD_USHR:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Shr, Destination = FpReg(arm.Rd, true), Sources = [FpReg(arm.Rn, true), Imm(arm.Immediate)], Annotation = "SIMD USHR" }); return;
            case Arm64Opcode.SIMD_MOVI:
                if (arm.Shift == 0b1111)
                {
                    int op = (int)((arm.RawValue >> 29) & 1);
                    bool isDouble = op == 1;
                    long rawBits = Arm64Instruction.DecodeFpImm8RawBits((byte)arm.Immediate, isDouble);
                    long rawLo, rawHi;
                    if (isDouble)
                    {
                        rawLo = rawBits;
                        rawHi = arm.Is64Bit ? rawBits : 0;
                    }
                    else
                    {
                        rawLo = (rawBits << 32) | rawBits;
                        rawHi = arm.Is64Bit ? rawLo : 0;
                    }
                    Emit(IrInstruction.CreateLoadImm(addr, FpReg(arm.Rd, true), IrOperand.SimdImmediate(rawLo, rawHi)));
                }
                else
                {
                    Emit(IrInstruction.CreateLoadImm(addr, FpReg(arm.Rd, true), IrOperand.SimdImmediate(arm.Immediate, arm.Immediate)));
                }
                return;
            case Arm64Opcode.SIMD_EXT:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Assign, Destination = FpReg(arm.Rd, true), Sources = [FpReg(arm.Rn, true), FpReg(arm.Rm, true)], Annotation = "EXT" }); return;

            // ── Floating-Point Arithmetic ──
            case Arm64Opcode.FADD: Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FAdd, FpReg(arm.Rd, arm.Is64Bit), FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit))); return;
            case Arm64Opcode.FSUB: Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FSub, FpReg(arm.Rd, arm.Is64Bit), FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit))); return;
            case Arm64Opcode.FMUL: Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FMul, FpReg(arm.Rd, arm.Is64Bit), FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit))); return;
            case Arm64Opcode.FDIV: Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FDiv, FpReg(arm.Rd, arm.Is64Bit), FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit))); return;

            // FSQRT
            case Arm64Opcode.FSQRT:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FSqrt, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit)] }); return;

            // FMADD: Rd = Ra + Rn * Rm (fused multiply-add)
            // FMSUB: Rd = Ra - Rn * Rm (fused multiply-subtract)
            // Ra is stored in arm.Shift (same convention as MADD)
            // Sources: [Rn, Rm, Ra] — ExprPropagator builds "Ra + Rn * Rm"
            case Arm64Opcode.FMADD:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FMulAdd, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit), FpReg(arm.Shift, arm.Is64Bit)] }); return;
            case Arm64Opcode.FMSUB:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FMulSub, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit), FpReg(arm.Shift, arm.Is64Bit)] }); return;
            // FNMADD: Rd = -(Ra + Rn * Rm) → lift as FNeg(FMulAdd)
            case Arm64Opcode.FNMADD:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FMulAdd, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit), FpReg(arm.Shift, arm.Is64Bit)], Annotation = "negated" }); return;
            // FNMSUB: Rd = -(Ra - Rn * Rm) = Rn * Rm - Ra → lift as regular FMulSub-negated
            case Arm64Opcode.FNMSUB:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FMulSub, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit), FpReg(arm.Shift, arm.Is64Bit)], Annotation = "negated" }); return;

            // FNEG
            case Arm64Opcode.FNEG:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FNeg, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit)] }); return;

            // FABS
            case Arm64Opcode.FABS:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FAbs, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit)] }); return;

            // FCMP — float compare
            case Arm64Opcode.FCMP:
                if (arm.Rm == 0xFF)
                { Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FCompare, Sources = [FpReg(arm.Rn, arm.Is64Bit), IrOperand.FloatImmediate(0, (byte)(arm.Is64Bit ? 64 : 32))], Annotation = "vs 0.0" }); return; }
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FCompare, Sources = [FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit)] }); return;

            // FCCMP — FP conditional compare
            case Arm64Opcode.FCCMP:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FCompare, Sources = [IrOperand.Condition(arm.Condition), FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit)], Annotation = "FCCMP" }); return;

            // FCSEL — float conditional select
            case Arm64Opcode.FCSEL:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Select, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [IrOperand.Condition(arm.Condition), FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit)], Annotation = "FCSEL" }); return;

            // ── Float ↔ Int Conversion ──
            case Arm64Opcode.SCVTF:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.SignedIntToFloat, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [Reg(arm.Rn, arm.Is64Bit)] }); return;
            case Arm64Opcode.UCVTF:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.UnsignedIntToFloat, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [Reg(arm.Rn, arm.Is64Bit)] }); return;
            case Arm64Opcode.FCVTZS:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FloatToSignedInt, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit)] }); return;
            case Arm64Opcode.FCVTZU:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FloatToUnsignedInt, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit)] }); return;
            case Arm64Opcode.FCVT_PREC:
                Emit(new IrInstruction { Address = addr, Opcode = arm.Is64Bit ? IrOpcode.FloatExtend : IrOpcode.FloatTruncate, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, !arm.Is64Bit)] }); return;

            // FMOV variants
            case Arm64Opcode.FMOV_FP_REG:
                Emit(IrInstruction.CreateAssign(addr, FpReg(arm.Rd, arm.Is64Bit), FpReg(arm.Rn, arm.Is64Bit))); return;
            case Arm64Opcode.FMOV_GP_TO_FP:
                // FMOV Sd, Wn — when Wn==WZR (reg31), this is FMOV Sd, #0.0
                if (arm.Rn == 31)
                { Emit(IrInstruction.CreateLoadImm(addr, FpReg(arm.Rd, arm.Is64Bit), IrOperand.FloatImmediate(0, (byte)(arm.Is64Bit ? 64 : 32)))); return; }
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Bitcast, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [Reg(arm.Rn, arm.Is64Bit)] }); return;
            case Arm64Opcode.FMOV_FP_TO_GP:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Bitcast, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit)] }); return;
            // Bug 2 fix: decode ARM64 imm8 float encoding to proper IEEE754 bits
            case Arm64Opcode.FMOV_FP_CONST:
            case Arm64Opcode.FMOV_IMM:
                { long rawBits = Arm64Instruction.DecodeFpImm8RawBits((byte)arm.Immediate, arm.Is64Bit);
                Emit(IrInstruction.CreateLoadImm(addr, FpReg(arm.Rd, arm.Is64Bit), IrOperand.FloatImmediate(rawBits, (byte)(arm.Is64Bit ? 64 : 32)))); return; }

            // MRS — system register read (typically TPIDR_EL0 for TLS)
            case Arm64Opcode.MRS:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Assign, Destination = Reg(arm.Rd, true), Sources = [IrOperand.NamedRegister(-1, 64, "SYSREG")], Annotation = "MRS" }); return;

            // ── Remaining integer ops ──
            case Arm64Opcode.ADDS_IMM:
                if (arm.Rd == 31) // CMN alias — compare (add, set flags, discard result)
                { Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Compare, Sources = [Reg(arm.Rn, arm.Is64Bit), Imm(arm.Immediate)], Annotation = "CMN" }); return; }
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Add, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [Reg(arm.Rn, arm.Is64Bit), Imm(arm.Immediate)], Annotation = "sets flags" }); return;
            case Arm64Opcode.ADDS_REG:
                if (arm.Rd == 31) // CMN alias
                { Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Compare, Sources = [Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit)], Annotation = "CMN" }); return; }
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Add, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit)], Annotation = "sets flags" }); return;
            case Arm64Opcode.MSUB:
            {
                if (arm.Shift == 31)
                {
                    int tmpReg = (arm.Rn == 16 || arm.Rm == 16) ? 17 : 16;
                    var mulTmp = IrOperand.Register(tmpReg, arm.Is64Bit);
                    Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Mul, mulTmp, Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit)));
                    Emit(new IrInstruction { Address = addr + 1, Opcode = IrOpcode.Neg, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [mulTmp], Annotation = "MNEG" });
                    return;
                }
                int tmpReg2 = (arm.Rn == 16 || arm.Rm == 16 || arm.Shift == 16) ? 17 : 16;
                var mulTmp2 = IrOperand.Register(tmpReg2, arm.Is64Bit);
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Mul, mulTmp2, Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit)));
                Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Sub, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Shift, arm.Is64Bit), mulTmp2));
                return;
            }
            case Arm64Opcode.UMULL:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.UMulWide, Reg(arm.Rd, true), Reg(arm.Rn, false), Reg(arm.Rm, false))); return;
            case Arm64Opcode.SMULH:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.SMulHigh, Reg(arm.Rd, true), Reg(arm.Rn, true), Reg(arm.Rm, true))); return;
            case Arm64Opcode.UMULH:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.UMulHigh, Reg(arm.Rd, true), Reg(arm.Rn, true), Reg(arm.Rm, true))); return;
            case Arm64Opcode.CSNEG:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.SelectNeg, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [IrOperand.Condition(arm.Condition), DataReg(arm.Rn, arm.Is64Bit), DataReg(arm.Rm, arm.Is64Bit)] }); return;
            case Arm64Opcode.ADR:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.LoadAddress, Destination = Reg(arm.Rd, true), Sources = [IrOperand.Label((ulong)arm.Immediate)] }); return;
            case Arm64Opcode.LDR_LIT:
                Emit(IrInstruction.CreateLoad(addr, Reg(arm.Rd, arm.Is64Bit), IrOperand.AnnotatedMemory(0, arm.Immediate, (byte)(arm.Is64Bit ? 64 : 32), "PC-relative literal"))); return;
            case Arm64Opcode.LDR_LIT_FP:
            {
                byte sw = SimdWidth(arm);
                if (sw == 128)
                {
                    long rawLo = 0, rawHi = 0;
                    if (_elf != null && _binaryBytes != null)
                    {
                        long fileOffset = _elf.VirtualToFileOffset((ulong)arm.Immediate);
                        if (fileOffset >= 0 && fileOffset + 16 <= _binaryBytes.Length)
                        {
                            rawLo = BitConverter.ToInt64(_binaryBytes, (int)fileOffset);
                            rawHi = BitConverter.ToInt64(_binaryBytes, (int)fileOffset + 8);
                        }
                    }
                    Emit(IrInstruction.CreateLoadImm(addr, IrOperand.FpRegister(arm.Rd, sw), IrOperand.SimdImmediate(rawLo, rawHi)));
                }
                else
                {
                    long rawBits = 0;
                    if (_elf != null && _binaryBytes != null)
                    {
                        long fileOffset = _elf.VirtualToFileOffset((ulong)arm.Immediate);
                        if (fileOffset >= 0 && fileOffset + (sw >= 64 ? 8 : 4) <= _binaryBytes.Length)
                        {
                            rawBits = sw >= 64
                                ? BitConverter.ToInt64(_binaryBytes, (int)fileOffset)
                                : (long)(uint)BitConverter.ToInt32(_binaryBytes, (int)fileOffset);
                        }
                    }
                    Emit(IrInstruction.CreateLoadImm(addr, IrOperand.FpRegister(arm.Rd, sw), IrOperand.FloatImmediate(rawBits, sw)));
                }
                return;
            }
            case Arm64Opcode.LDRSB_IMM:
            {
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Load, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [Mem(arm.Rn, offset, 8)], Annotation = "sign-extend 8" }); 
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }
            case Arm64Opcode.LDRSH_IMM:
            {
                long offset = arm.Writeback == 1 ? 0 : arm.Immediate;
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Load, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [Mem(arm.Rn, offset, 16)], Annotation = "sign-extend 16" }); 
                if (arm.Writeback != 0 && arm.Rn < 31) { Emit(IrInstruction.CreateBinOp(addr + 1, IrOpcode.Add, Reg(arm.Rn, true), Reg(arm.Rn, true), Imm(arm.Immediate))); _adrpPageValid[arm.Rn] = false; }
                return;
            }
            case Arm64Opcode.CCMP:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Compare, Sources = [IrOperand.Condition(arm.Condition), Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit)], Annotation = "CCMP" }); return;
            case Arm64Opcode.CCMN:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Compare, Sources = [IrOperand.Condition(arm.Condition), Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit)], Annotation = "CCMN" }); return;
            case Arm64Opcode.CCMN_IMM:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Compare, Sources = [IrOperand.Condition(arm.Condition), Reg(arm.Rn, arm.Is64Bit), Imm(arm.Rm)], Annotation = "CCMN" }); return;
            case Arm64Opcode.BFM:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.BitfieldInsert, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [Reg(arm.Rn, arm.Is64Bit), Imm(arm.Rm), Imm(arm.Shift)] }); return;
            case Arm64Opcode.EXTR:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Ror, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit), Imm(arm.Shift)], Annotation = "EXTR" }); return;
            case Arm64Opcode.RORV:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Ror, Reg(arm.Rd, arm.Is64Bit), Reg(arm.Rn, arm.Is64Bit), Reg(arm.Rm, arm.Is64Bit))); return;

            // ── Remaining FP ops ──
            case Arm64Opcode.FNMUL:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FNMul, FpReg(arm.Rd, arm.Is64Bit), FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit))); return;
            case Arm64Opcode.FMAX:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FMax, FpReg(arm.Rd, arm.Is64Bit), FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit))); return;
            case Arm64Opcode.FMIN:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FMin, FpReg(arm.Rd, arm.Is64Bit), FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit))); return;
            case Arm64Opcode.FMAXNM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FMax, FpReg(arm.Rd, arm.Is64Bit), FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit))); return;
            case Arm64Opcode.FMINNM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FMin, FpReg(arm.Rd, arm.Is64Bit), FpReg(arm.Rn, arm.Is64Bit), FpReg(arm.Rm, arm.Is64Bit))); return;
            case Arm64Opcode.FRINTN:
            case Arm64Opcode.FRINTP:
            case Arm64Opcode.FRINTM:
            case Arm64Opcode.FRINTZ:
            case Arm64Opcode.FRINTA:
            case Arm64Opcode.FRINTX:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FRound, Destination = FpReg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit)], Annotation = arm.Opcode.ToString() }); return;
            case Arm64Opcode.FCVTNS:
            case Arm64Opcode.FCVTPS:
            case Arm64Opcode.FCVTMS:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FloatToSignedInt, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit)], Annotation = arm.Opcode.ToString() }); return;
            case Arm64Opcode.FCVTNU:
            case Arm64Opcode.FCVTPU:
            case Arm64Opcode.FCVTMU:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FloatToUnsignedInt, Destination = Reg(arm.Rd, arm.Is64Bit), Sources = [FpReg(arm.Rn, arm.Is64Bit)], Annotation = arm.Opcode.ToString() }); return;

            default:
                Console.WriteLine($"[LIFTER-UNKNOWN-ARM64] Unknown instruction at 0x{addr:X}: {arm}");
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Unknown, Annotation = arm.ToString() }); return;
        }
    }

    /// <summary>Reg 31 as base pointer (memory addressing) = SP.</summary>
    private IrOperand Reg(int regNum, bool is64) => regNum == 31 ? IrOperand.StackPointer() : IrOperand.Register(regNum, is64);
    /// <summary>Reg 31 as data value (MOV src, STR val, STP val, Rm in arith) = XZR (zero).</summary>
    private IrOperand DataReg(int regNum, bool is64) => regNum == 31 ? IrOperand.Zero(is64) : IrOperand.Register(regNum, is64);
    private IrOperand FpReg(int regNum, bool is64) => IrOperand.FpRegister(regNum, (byte)(is64 ? 64 : 32));
    private string RegStr(int regNum) => regNum == 31 ? "SP" : $"X{regNum}";
    private IrOperand Imm(long val) => IrOperand.Immediate(val);
    private IrOperand Mem(int baseReg, long offset, byte width) => IrOperand.Memory(baseReg, offset, width);

    /// <summary>Derive SIMD access width in bits from Shift field.
    /// Decoder sets: 0=B(8), 1=H(16), 2=S(32), 3=D(64), 4=Q(128).</summary>
    private static byte SimdWidth(Arm64Instruction arm) => arm.Shift switch
    {
        0 => 8,   // B register
        1 => 16,  // H register
        2 => 32,  // S register
        3 => 64,  // D register
        4 => 128, // Q register
        _ => (byte)(arm.Is64Bit ? 64 : 32)
    };

    /// <summary>Does this instruction write to its Rd field?</summary>
    private static bool WritesToRd(in Arm64Instruction inst) => inst.Opcode switch
    {
        Arm64Opcode.MOVZ or Arm64Opcode.MOVK or Arm64Opcode.MOVN => true,
        Arm64Opcode.MOV_REG => true,
        Arm64Opcode.ADD_IMM or Arm64Opcode.ADD_REG or Arm64Opcode.SUB_IMM or Arm64Opcode.SUB_REG => true,
        Arm64Opcode.ADDS_IMM or Arm64Opcode.ADDS_REG or Arm64Opcode.SUBS_IMM or Arm64Opcode.SUBS_REG => true,
        Arm64Opcode.AND_IMM or Arm64Opcode.AND_REG or Arm64Opcode.ORR_IMM or Arm64Opcode.ORR_REG or
        Arm64Opcode.BIC_REG or Arm64Opcode.ORN_REG or Arm64Opcode.EOR_REG or Arm64Opcode.EON_REG or
        Arm64Opcode.ANDS_REG or Arm64Opcode.BICS_REG => true,
        Arm64Opcode.EOR_IMM or Arm64Opcode.EOR_REG or Arm64Opcode.ANDS_IMM => true,
        Arm64Opcode.UBFM or Arm64Opcode.SBFM or Arm64Opcode.BFM or Arm64Opcode.EXTR => true,
        Arm64Opcode.MADD or Arm64Opcode.MSUB or Arm64Opcode.SDIV or Arm64Opcode.UDIV => true,
        Arm64Opcode.SMULL or Arm64Opcode.UMULL or Arm64Opcode.SMULH or Arm64Opcode.UMULH => true,
        Arm64Opcode.CSEL or Arm64Opcode.CSINC or Arm64Opcode.CSINV or Arm64Opcode.CSNEG => true,
        Arm64Opcode.LDR_IMM or Arm64Opcode.LDR_REG or Arm64Opcode.LDR_LIT or Arm64Opcode.LDR_LIT_FP => true,
        Arm64Opcode.LDRB_IMM or Arm64Opcode.LDRH_IMM or Arm64Opcode.LDRSW_IMM => true,
        Arm64Opcode.LDRSB_IMM or Arm64Opcode.LDRSH_IMM => true,
        Arm64Opcode.ADRP or Arm64Opcode.ADR => true,
        Arm64Opcode.LSLV or Arm64Opcode.LSRV or Arm64Opcode.ASRV or Arm64Opcode.RORV => true,
        Arm64Opcode.MRS => true,
        Arm64Opcode.LDP or Arm64Opcode.LDP_SIMD => true, // LDP also writes to Rm!
        Arm64Opcode.SIMD_UMOV or Arm64Opcode.SIMD_FADDP => true,
        _ => false,
    };

    // ── Known C math library functions that return float via s0 (FP register) ──
    // These are libc/libm functions linked directly by IL2CPP's Clang codegen.
    // Mathf.Sin → sinf, Mathf.Cos → cosf, Mathf.Sqrt → sqrtf, etc.
    private static readonly HashSet<string> _floatReturningFunctions = new(StringComparer.Ordinal)
    {
        "sinf", "cosf", "tanf", "sqrtf", "fabsf", "floorf", "ceilf", "roundf",
        "fmodf", "powf", "expf", "logf", "log2f", "log10f", "atan2f", "asinf", "acosf", "atanf",
        "sin", "cos", "tan", "sqrt", "fabs", "floor", "ceil", "round",
        "fmod", "pow", "exp", "log", "log2", "log10", "atan2", "asin", "acos", "atan",
    };

    /// <summary>
    /// Determine if a call returns a float/double value via FP register (s0/d0).
    /// Uses metadata-resolved return type first, then falls back to known C math function names.
    /// </summary>
    private static bool IsFloatReturnType(string? resultType, string? methodName)
    {
        // Check metadata-resolved return type
        if (resultType != null)
        {
            if (resultType is "System.Single" or "System.Double" or "float" or "double" or
                "Single" or "Double")
                return true;
        }

        // Check known C math library function names (runtime helpers without metadata)
        if (methodName != null && _floatReturningFunctions.Contains(methodName))
            return true;

        return false;
    }

    // ── C math function → FP argument count ──
    // ARM64 AAPCS: float/double arguments are passed via s0-s7/d0-d7
    private static readonly Dictionary<string, int> _mathFpArgCounts = new(StringComparer.Ordinal)
    {
        // 1-arg float functions
        ["sinf"] = 1, ["cosf"] = 1, ["tanf"] = 1, ["sqrtf"] = 1,
        ["fabsf"] = 1, ["floorf"] = 1, ["ceilf"] = 1, ["roundf"] = 1,
        ["expf"] = 1, ["logf"] = 1, ["log2f"] = 1, ["log10f"] = 1,
        ["asinf"] = 1, ["acosf"] = 1, ["atanf"] = 1,
        // 2-arg float functions
        ["fmodf"] = 2, ["powf"] = 2, ["atan2f"] = 2,
        // 1-arg double functions
        ["sin"] = 1, ["cos"] = 1, ["tan"] = 1, ["sqrt"] = 1,
        ["fabs"] = 1, ["floor"] = 1, ["ceil"] = 1, ["round"] = 1,
        ["exp"] = 1, ["log"] = 1, ["log2"] = 1, ["log10"] = 1,
        ["asin"] = 1, ["acos"] = 1, ["atan"] = 1,
        // 2-arg double functions
        ["fmod"] = 2, ["pow"] = 2, ["atan2"] = 2,
    };

    /// <summary>Get the number of FP register arguments for a known C math function.</summary>
    private static int GetMathFpArgCount(string funcName) =>
        _mathFpArgCounts.TryGetValue(funcName, out int count) ? count : 0;

    // ── C math function → C# Mathf/Math equivalent name ──
    // Dynamically derives the C# name from the C function name instead of
    // maintaining a hardcoded dictionary. Format uses "." (not "::") to prevent
    // ExprPropagator from treating it as a managed call and stripping FP args.
    //
    // Rules:
    //   1. Special cases handled first (fabs→Abs, fmod→Repeat/IEEERemainder, ceil→Ceiling)
    //   2. Float variants end with 'f' → strip suffix → capitalize → "Mathf.Name"
    //   3. Double variants (no 'f') → capitalize → "Math.Name"
    private static string? MapCMathToCSharp(string funcName)
    {
        // Only map functions we know are math (must be in _mathFpArgCounts)
        if (!_mathFpArgCounts.ContainsKey(funcName)) return null;

        // Handle irregular C→C# name mappings first
        if (funcName == "fabsf") return "Mathf.Abs";
        if (funcName == "fabs") return "Math.Abs";
        if (funcName == "fmodf") return "Mathf.Repeat";
        if (funcName == "fmod") return "Math.IEEERemainder";
        if (funcName == "ceilf") return "Mathf.Ceil";
        if (funcName == "ceil") return "Math.Ceiling";
        if (funcName == "log2f") return "Mathf.Log"; // Unity has no Mathf.Log2; caller uses 2-arg overload
        if (funcName == "log10f") return "Mathf.Log10";

        // Is it a single-precision (float) variant? → ends with 'f'
        bool isFloat = funcName.EndsWith('f');

        // Strip the 'f' suffix for float variants to get the base name
        string baseName = isFloat ? funcName[..^1] : funcName;

        // Capitalize first letter (e.g., "sin" → "Sin", "atan2" → "Atan2")
        if (baseName.Length > 0)
            baseName = char.ToUpperInvariant(baseName[0]) + baseName[1..];

        return isFloat ? $"Mathf.{baseName}" : $"Math.{baseName}";
    }

    /// <summary>Returns true if the C function name is a double-precision variant (uses d0-d7).
    /// Double variants don't have the 'f' suffix (sin vs sinf). Only valid for known math functions.</summary>
    private static bool IsMathDoubleVariant(string funcName) =>
        _mathFpArgCounts.ContainsKey(funcName) && !funcName.EndsWith('f');
}
