using System;
using System.Collections.Generic;
using Rosetta.Binary;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Lifter.ClangRules;
using Rosetta.Metadata;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR;

/// <summary>
/// Lifts ARM32 Thumb2 instructions to architecture-neutral IR.
///
/// Key differences from ARM64 IrLifter:
///   1. Registers: R0-R15 (32-bit), SP=R13, LR=R14, PC=R15
///   2. Calling convention: AAPCS — R0-R3 for args, R0 for return (4 GP regs, not 8)
///   3. Immediate loads: MOVW+MOVT (2 instrs, always 32-bit result)
///   4. PC-relative: LDR Rx,[PC,#off] (literal pool, not ADRP+ADD)
///   5. Prologue: PUSH {R4-R7,LR} / Epilogue: POP {R4-R7,PC}
///   6. FP: VFP coprocessor (VLDR/VADD/VMUL) not AdvSIMD (LDR Sn/FADD)
///   7. IT blocks: conditional execution prefix (not B.cond)
///   8. No MOVZ/MOVK chains — just MOVW (bottom 16) + MOVT (top 16)
/// </summary>
public sealed class Thumb2IrLifter
{
    private readonly CallResolver _callResolver;
    private readonly IBinaryParser? _elf;
    private readonly byte[]? _binaryBytes;
    private readonly List<IrInstruction> _pending = new(2);

    // ── MOVW/MOVT chain tracking ──
    // ARM32 compilers emit MOVW+MOVT pairs to load 32-bit constants.
    // Always exactly 2 instructions (not interleaved like ARM64 MOVZ/MOVK).
    private readonly uint[] _movwAccum = new uint[16];  // accumulated value (from MOVW)
    private readonly bool[] _movwActive = new bool[16]; // is MOVW pending for this register?
    private readonly ulong[] _movwAddr = new ulong[16]; // address of MOVW instruction

    // ── LDR_LIT tracking for PIC address resolution ──
    // ARM32 PIC: LDR Rd,[PC,#off] → ADD Rd,PC → resolves to absolute address.
    // This is the ARM32 equivalent of ARM64's ADRP+ADD pattern.
    private readonly long[] _litPoolValue = new long[16];  // value loaded from literal pool
    private readonly bool[] _litPoolActive = new bool[16]; // is LDR_LIT pending for this register?
    private readonly ulong[] _litPoolAddr = new ulong[16]; // instruction address of the ADD_PC instruction

    // ── Current method context ──
    private string _currentReturnType = "void";
    private bool _currentIsStatic;

    // ── IT block tracking ──
    private byte _itCondition;   // condition for current IT block
    private int _itRemaining;    // instructions remaining in IT block

    public Thumb2IrLifter(CallResolver callResolver, IBinaryParser? elf = null, byte[]? binaryBytes = null)
    {
        _callResolver = callResolver;
        _elf = elf;
        _binaryBytes = binaryBytes;
    }

    public IrMethod Lift(MethodDefinition methodDef, ulong methodVA, Thumb2Instruction[] instrs, string methodName, string? declaringType, string returnType, List<string> parameters, bool isStatic)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"Thumb2IrLifter.Lift: {declaringType}::{methodName} (VA=0x{methodVA:X}, {instrs.Length} instrs)");
        var instructions = new List<IrInstruction>(instrs.Length);
        int collapsed = 0;
        _currentReturnType = returnType;
        _currentIsStatic = isStatic;
        _itRemaining = 0;

        Array.Clear(_movwActive);
        Array.Clear(_litPoolActive);

        for (int i = 0; i < instrs.Length; i++)
        {
            var t = instrs[i];
            _pending.Clear();

            // Track IT block countdown
            if (t.Opcode != Thumb2Opcode.IT && _itRemaining > 0)
                _itRemaining--;

            TranslateInstruction(t, instrs, ref i, methodVA, instructions);
            foreach (var ir in _pending)
            {
                instructions.Add(ir);
                if (ir.CollapsedCount > 1)
                    collapsed += (ir.CollapsedCount - 1);
            }
        }

        // Flush remaining MOVW chains
        _pending.Clear();
        FlushAllMovwChains();
        foreach (var ir in _pending)
            instructions.Add(ir);

        var result = new IrMethod
        {
            MethodName = methodName, DeclaringType = declaringType, ReturnType = returnType,
            Parameters = parameters, IsStatic = isStatic, EntryAddress = methodVA,
            Token = methodDef.Token, TypeDefIndex = methodDef.DeclaringTypeIndex,
            MethodIndex = methodDef.GlobalIndex,
            Instructions = instructions,
            OriginalInstructionCount = instrs.Length, CollapsedInstructionCount = collapsed,
            IsArm32 = true
        };
        result.BuildParamMaps();

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  Thumb2IrLifter: Lifted {instrs.Length} Thumb2 instructions to {instructions.Count} IR instructions");
        return result;
    }

    private void Emit(IrInstruction ir) => _pending.Add(ir);

    // ════════════════════════════════════════════════════════════════════
    // MOVW/MOVT chain management
    // ════════════════════════════════════════════════════════════════════

    private void FlushMovwChain(int rd)
    {
        if (rd >= 16 || !_movwActive[rd]) return;
        _movwActive[rd] = false;

        Emit(new IrInstruction
        {
            Address = _movwAddr[rd], Opcode = IrOpcode.LoadImmediate,
            Destination = Reg(rd),
            Sources = [IrOperand.Immediate(_movwAccum[rd], 32)],
            CollapsedCount = 1
        });
    }

    private void FlushAllMovwChains()
    {
        for (int r = 0; r < 16; r++)
            FlushMovwChain(r);
    }

    private void FlushIfSourced(int reg)
    {
        if (reg < 16 && _movwActive[reg])
            FlushMovwChain(reg);
    }

    // ════════════════════════════════════════════════════════════════════
    // Main instruction translator
    // ════════════════════════════════════════════════════════════════════

    private void TranslateInstruction(Thumb2Instruction t, Thumb2Instruction[] instrs, ref int i, ulong methodVA, List<IrInstruction> allInsts)
    {
        ulong addr = t.Address;

        // Flush MOVW chains when registers are consumed by non-MOVW/MOVT instructions
        if (t.Opcode != Thumb2Opcode.MOVW && t.Opcode != Thumb2Opcode.MOVT && t.Opcode != Thumb2Opcode.NOP)
        {
            if (t.Opcode is Thumb2Opcode.B or Thumb2Opcode.BL or Thumb2Opcode.BLX or Thumb2Opcode.BLX_REG
                or Thumb2Opcode.B_COND or Thumb2Opcode.BX or Thumb2Opcode.CBZ or Thumb2Opcode.CBNZ)
            {
                FlushAllMovwChains();
            }
            else
            {
                FlushIfSourced(t.Rn);
                if (t.Rm < 16) FlushIfSourced(t.Rm);
                // STR: Rd is a source (value being stored)
                if (t.Opcode is Thumb2Opcode.STR_IMM or Thumb2Opcode.STR_REG
                    or Thumb2Opcode.STRB_IMM or Thumb2Opcode.STRH_IMM
                    or Thumb2Opcode.STRD_IMM or Thumb2Opcode.VSTR)
                {
                    FlushIfSourced(t.Rd);
                }
                // If writing to a register with active chain, flush first
                if (WritesToRd(t) && t.Rd < 16 && _movwActive[t.Rd])
                    FlushMovwChain(t.Rd);
            }
        }

        // ── PUSH → StackAlloc ──
        if (t.Opcode == Thumb2Opcode.PUSH)
        {
            Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.StackAlloc, CollapsedCount = 1 });
            return;
        }

        // ── POP with PC → Return ──
        if (t.Opcode == Thumb2Opcode.POP)
        {
            if ((t.RegisterList & (1 << 15)) != 0) // PC in list = return
            {
                IrOperand? retVal = null;
                if (_currentReturnType != "void")
                {
                    bool isFpReturn = IsFloatReturnType(_currentReturnType);
                    retVal = isFpReturn ? FpReg(0, false) : Reg(0);
                }
                Emit(IrInstruction.CreateReturn(addr, retVal));
            }
            else
            {
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.StackFree, CollapsedCount = 1 });
            }
            return;
        }

        // ── SUB SP, SP, #imm → StackAlloc ──
        if (t.Opcode == Thumb2Opcode.SUB_IMM && t.Rd == 13 && t.Rn == 13)
        {
            Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.StackAlloc, CollapsedCount = 1 });
            return;
        }
        // ── ADD SP, SP, #imm → StackFree ──
        if (t.Opcode == Thumb2Opcode.ADD_IMM && t.Rd == 13 && t.Rn == 13)
        {
            Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.StackFree, CollapsedCount = 1 });
            return;
        }
        // ── STR LR, [SP, #off] → suppress (LR save) ──
        if (t.Opcode == Thumb2Opcode.STR_IMM && t.Rd == 14 && t.Rn == 13) return;
        // ── LDR LR, [SP, #off] → suppress (LR restore) ──
        if (t.Opcode == Thumb2Opcode.LDR_IMM && t.Rd == 14 && t.Rn == 13) return;
        // ── ADD R7, SP, #imm → frame pointer setup, suppress ──
        if (t.Opcode == Thumb2Opcode.ADD_IMM && t.Rd == 7 && t.Rn == 13) return;
        // ── STR FP, [SP, #off] → suppress ──
        if (t.Opcode == Thumb2Opcode.STR_IMM && t.Rd == 11 && t.Rn == 13) return;
        // ── LDR FP, [SP], #off → suppress (epilogue) ──
        if (t.Opcode == Thumb2Opcode.LDR_IMM && t.Rd == 11 && t.Rn == 13) return;

        switch (t.Opcode)
        {
            case Thumb2Opcode.NOP:
                Emit(IrInstruction.CreateNop(addr)); return;

            case Thumb2Opcode.IT:
                // Track IT block state — following instructions are conditionally executed
                _itCondition = t.Condition;
                _itRemaining = t.Shift; // number of instructions in IT block
                return; // IT itself produces no IR

            case Thumb2Opcode.DMB:
                return; // Memory barriers are irrelevant for decompilation

            // ── Immediate loads ──
            case Thumb2Opcode.MOVW:
            {
                int rd = t.Rd;
                if (rd < 16)
                {
                    FlushMovwChain(rd);
                    _movwAccum[rd] = (uint)(t.Immediate & 0xFFFF);
                    _movwActive[rd] = true;
                    _movwAddr[rd] = addr;

                    // Check if MOVT follows for this register
                    bool hasMovt = false;
                    for (int j = i + 1; j < instrs.Length && j <= i + 4; j++)
                    {
                        if (instrs[j].Opcode == Thumb2Opcode.MOVT && instrs[j].Rd == rd)
                        { hasMovt = true; break; }
                        if (WritesToRd(instrs[j]) && instrs[j].Rd == rd && instrs[j].Opcode != Thumb2Opcode.MOVT)
                            break;
                    }
                    if (!hasMovt)
                        FlushMovwChain(rd); // Standalone MOVW
                }
                return;
            }

            case Thumb2Opcode.MOVT:
            {
                int rd = t.Rd;
                if (rd < 16 && _movwActive[rd])
                {
                    _movwAccum[rd] = (_movwAccum[rd] & 0xFFFF) | ((uint)(t.Immediate & 0xFFFF) << 16);
                    // Always complete — MOVW+MOVT is exactly 2 instructions
                    var chainAddr = _movwAddr[rd];
                    _movwActive[rd] = false;
                    Emit(new IrInstruction
                    {
                        Address = chainAddr, Opcode = IrOpcode.LoadImmediate,
                        Destination = Reg(rd),
                        Sources = [IrOperand.Immediate(_movwAccum[rd], 32)],
                        CollapsedCount = 2
                    });
                }
                else
                {
                    // Standalone MOVT — unusual but handle it
                    Emit(IrInstruction.CreateLoadImm(addr, Reg(t.Rd),
                        IrOperand.Immediate(t.Immediate << 16, 32)));
                }
                return;
            }

            case Thumb2Opcode.MOV_IMM:
                Emit(IrInstruction.CreateLoadImm(addr, Reg(t.Rd), Imm(t.Immediate))); return;
            case Thumb2Opcode.MOV_REG:
                Emit(IrInstruction.CreateAssign(addr, Reg(t.Rd), DataReg(t.Rm))); return;
            case Thumb2Opcode.MVN_IMM:
                Emit(IrInstruction.CreateLoadImm(addr, Reg(t.Rd), Imm(~t.Immediate & 0xFFFFFFFF))); return;
            case Thumb2Opcode.MVN_REG:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Not, Destination = Reg(t.Rd), Sources = [DataReg(t.Rm)] }); return;
            case Thumb2Opcode.NEG:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Neg, Destination = Reg(t.Rd), Sources = [DataReg(t.Rm)] }); return;

            // ── Arithmetic ──
            case Thumb2Opcode.ADD_IMM:
                if (t.Rn == 15) // ADD Rd, PC, #imm — PC-relative address
                {
                    Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.LoadAddress, Destination = Reg(t.Rd), Sources = [IrOperand.Label((ulong)t.Immediate)] }); return;
                }
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Add, Reg(t.Rd), Reg(t.Rn), Imm(t.Immediate))); return;
            case Thumb2Opcode.ADD_REG:
                // ADD Rd, PC, Rm  or  ADD Rd, Rm, PC — PIC address resolution
                // Pattern: LDR Rd,[PC,#off] → ADD Rd,PC,Rd → absolute address
                // Capstone: add r4, pc, r4 → rd=4, rn=15(PC), rm=4
                if ((t.Rn == 15 || t.Rm == 15) && t.Rd < 16 && _litPoolActive[t.Rd])
                {
                    // Fuse: LDR_LIT loaded a GOT offset, ADD PC resolves to absolute address
                    // ARM mode: PC = instr_address + 8 (3-stage pipeline)
                    // Thumb mode: PC = instr_address + 4
                    ulong pcAtAdd = t.Address + 8; // ARM32 ARM mode: PC is instr+8
                    ulong resolved = (ulong)_litPoolValue[t.Rd] + pcAtAdd;
                    _litPoolActive[t.Rd] = false;
                    Emit(new IrInstruction
                    {
                        Address = _litPoolAddr[t.Rd], Opcode = IrOpcode.LoadAddress,
                        Destination = Reg(t.Rd),
                        Sources = [IrOperand.Label(resolved)],
                        CollapsedCount = 2
                    }); return;
                }
                // ADD Rd, PC, Rm → special case
                if (t.Rn == 15)
                {
                    Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Add, Destination = Reg(t.Rd), Sources = [IrOperand.NamedRegister(15, 32, "PC"), DataReg(t.Rm)] }); return;
                }
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Add, Reg(t.Rd), Reg(t.Rn), DataReg(t.Rm))); return;
            case Thumb2Opcode.SUB_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Sub, Reg(t.Rd), Reg(t.Rn), Imm(t.Immediate))); return;
            case Thumb2Opcode.SUB_REG:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Sub, Reg(t.Rd), Reg(t.Rn), DataReg(t.Rm))); return;
            case Thumb2Opcode.ADC_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Add, Reg(t.Rd), Reg(t.Rn), Imm(t.Immediate))); return;
            case Thumb2Opcode.ADC_REG:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Add, Reg(t.Rd), Reg(t.Rn), DataReg(t.Rm))); return;
            case Thumb2Opcode.SBC_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Sub, Reg(t.Rd), Reg(t.Rn), Imm(t.Immediate))); return;
            case Thumb2Opcode.SBC_REG:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Sub, Reg(t.Rd), Reg(t.Rn), DataReg(t.Rm))); return;
            case Thumb2Opcode.MUL:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Mul, Reg(t.Rd), Reg(t.Rn), Reg(t.Rm))); return;
            case Thumb2Opcode.MLA:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Add, Reg(t.Rd), Reg(t.Shift),
                    IrOperand.NamedRegister(-1, 32, $"MUL({RegStr(t.Rn)}, {RegStr(t.Rm)})"))); return;
            case Thumb2Opcode.SDIV:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.SDiv, Reg(t.Rd), Reg(t.Rn), Reg(t.Rm))); return;
            case Thumb2Opcode.UDIV:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.UDiv, Reg(t.Rd), Reg(t.Rn), Reg(t.Rm))); return;

            // ── Bitwise ──
            case Thumb2Opcode.AND_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.And, Reg(t.Rd), Reg(t.Rn), Imm(t.Immediate))); return;
            case Thumb2Opcode.AND_REG:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.And, Reg(t.Rd), Reg(t.Rn), DataReg(t.Rm))); return;
            case Thumb2Opcode.ORR_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Or, Reg(t.Rd), Reg(t.Rn), Imm(t.Immediate))); return;
            case Thumb2Opcode.ORR_REG:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Or, Reg(t.Rd), Reg(t.Rn), DataReg(t.Rm))); return;
            case Thumb2Opcode.EOR_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Xor, Reg(t.Rd), Reg(t.Rn), Imm(t.Immediate))); return;
            case Thumb2Opcode.EOR_REG:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Xor, Reg(t.Rd), Reg(t.Rn), DataReg(t.Rm))); return;
            case Thumb2Opcode.BIC_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.And, Reg(t.Rd), Reg(t.Rn), Imm(~t.Immediate & 0xFFFFFFFF))); return;
            case Thumb2Opcode.BIC_REG:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.And, Destination = Reg(t.Rd), Sources = [Reg(t.Rn), IrOperand.NamedRegister(-1, 32, $"~{RegStr(t.Rm)}")] }); return;
            case Thumb2Opcode.LSL_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Shl, Reg(t.Rd), Reg(t.Rn), Imm(t.Immediate))); return;
            case Thumb2Opcode.LSL_REG:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Shl, Reg(t.Rd), Reg(t.Rn), DataReg(t.Rm))); return;
            case Thumb2Opcode.LSR_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Shr, Reg(t.Rd), Reg(t.Rn), Imm(t.Immediate))); return;
            case Thumb2Opcode.LSR_REG:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Shr, Reg(t.Rd), Reg(t.Rn), DataReg(t.Rm))); return;
            case Thumb2Opcode.ASR_IMM:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Sar, Reg(t.Rd), Reg(t.Rn), Imm(t.Immediate))); return;
            case Thumb2Opcode.ASR_REG:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.Sar, Reg(t.Rd), Reg(t.Rn), DataReg(t.Rm))); return;

            // ── Memory ──
            case Thumb2Opcode.LDR_IMM:
                Emit(IrInstruction.CreateLoad(addr, Reg(t.Rd), Mem(t.Rn, t.Immediate, 32))); return;
            case Thumb2Opcode.STR_IMM:
                Emit(IrInstruction.CreateStore(addr, Mem(t.Rn, t.Immediate, 32), DataReg(t.Rd))); return;
            case Thumb2Opcode.LDRB_IMM:
                Emit(IrInstruction.CreateLoad(addr, Reg(t.Rd), Mem(t.Rn, t.Immediate, 8))); return;
            case Thumb2Opcode.STRB_IMM:
                Emit(IrInstruction.CreateStore(addr, Mem(t.Rn, t.Immediate, 8), DataReg(t.Rd))); return;
            case Thumb2Opcode.LDRH_IMM:
                Emit(IrInstruction.CreateLoad(addr, Reg(t.Rd), Mem(t.Rn, t.Immediate, 16))); return;
            case Thumb2Opcode.STRH_IMM:
                Emit(IrInstruction.CreateStore(addr, Mem(t.Rn, t.Immediate, 16), DataReg(t.Rd))); return;
            case Thumb2Opcode.LDRSB_IMM:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Load, Destination = Reg(t.Rd), Sources = [Mem(t.Rn, t.Immediate, 8)], Annotation = "sign-extend 8" }); return;
            case Thumb2Opcode.LDRSH_IMM:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Load, Destination = Reg(t.Rd), Sources = [Mem(t.Rn, t.Immediate, 16)], Annotation = "sign-extend 16" }); return;
            case Thumb2Opcode.LDR_REG:
                // ARM32 PIC pattern: LDR Rd, [PC, Rd] — GOT-relative load
                // The register Rm holds an offset loaded from literal pool.
                // Effective address = PC + 8 + offset → points to a GOT entry
                if (t.Rn == 15 && t.Rm < 16 && _litPoolActive[t.Rm])
                {
                    // Fuse: resolve the GOT-relative address
                    ulong pcAtLdr = t.Address + 8; // ARM mode: PC = instr + 8
                    ulong gotAddr = (ulong)_litPoolValue[t.Rm] + pcAtLdr;
                    _litPoolActive[t.Rm] = false;

                    // Read the GOT entry value from the binary
                    if (_elf != null && _binaryBytes != null)
                    {
                        long gotFileOffset = _elf.VirtualToFileOffset(gotAddr);
                        if (gotFileOffset >= 0 && gotFileOffset + 4 <= _binaryBytes.Length)
                        {
                            uint gotValue = BitConverter.ToUInt32(_binaryBytes, (int)gotFileOffset);
                            Emit(new IrInstruction
                            {
                                Address = _litPoolAddr[t.Rm], Opcode = IrOpcode.LoadAddress,
                                Destination = Reg(t.Rd),
                                Sources = [IrOperand.Label(gotValue)],
                                CollapsedCount = 2
                            }); return;
                        }
                    }

                    // Fallback: emit as load from resolved address
                    Emit(IrInstruction.CreateLoad(addr, Reg(t.Rd),
                        IrOperand.AnnotatedMemory(0, (long)gotAddr, 32, "GOT-relative"))); return;
                }
                Emit(IrInstruction.CreateLoad(addr, Reg(t.Rd),
                    IrOperand.AnnotatedMemory(t.Rn, 0, 32, $"[{RegStr(t.Rn)} + {RegStr(t.Rm)}]"))); return;
            case Thumb2Opcode.STR_REG:
                Emit(IrInstruction.CreateStore(addr,
                    IrOperand.AnnotatedMemory(t.Rn, 0, 32, $"[{RegStr(t.Rn)} + {RegStr(t.Rm)}]"),
                    DataReg(t.Rd))); return;

            // ── LDR literal (PC-relative) ──
            case Thumb2Opcode.LDR_LIT:
            {
                // Resolve the literal pool value from the binary
                if (_elf != null && _binaryBytes != null)
                {
                    long fileOffset = _elf.VirtualToFileOffset((ulong)t.Immediate);
                    if (fileOffset >= 0 && fileOffset + 4 <= _binaryBytes.Length)
                    {
                        uint litValue = BitConverter.ToUInt32(_binaryBytes, (int)fileOffset);

                        // Check if next instruction uses the loaded offset with PC (PIC patterns):
                        // Pattern 1: ADD Rd, PC, Rd → absolute address (ADD_REG with rn=15 or rm=15)
                        // Pattern 2: LDR Rd, [PC, Rd] → GOT-relative load (LDR_REG with rn=15, rm=Rd)
                        bool hasPcFusion = false;
                        if (t.Rd < 16)
                        {
                            for (int j = i + 1; j < instrs.Length && j <= i + 3; j++)
                            {
                                var next = instrs[j];
                                // Pattern 1: ADD Rd, PC, Rd
                                if (next.Opcode == Thumb2Opcode.ADD_REG && next.Rd == t.Rd &&
                                    (next.Rn == 15 || next.Rm == 15))
                                { hasPcFusion = true; break; }
                                // Pattern 2: LDR Rd, [PC, Rd] (GOT load)
                                if (next.Opcode == Thumb2Opcode.LDR_REG && next.Rn == 15 && next.Rm == t.Rd)
                                { hasPcFusion = true; break; }
                                // If the register is overwritten by something else, stop
                                if (WritesToRd(next) && next.Rd == t.Rd) break;
                            }
                        }

                        if (hasPcFusion && t.Rd < 16)
                        {
                            // Defer — store literal value for PIC fusion
                            _litPoolValue[t.Rd] = litValue;
                            _litPoolActive[t.Rd] = true;
                            _litPoolAddr[t.Rd] = addr;
                            return;
                        }

                        // No ADD PC follows — emit as immediate load
                        Emit(IrInstruction.CreateLoadImm(addr, Reg(t.Rd),
                            IrOperand.Immediate(litValue, 32)));
                        return;
                    }
                }
                // Fallback: emit as load from address
                Emit(IrInstruction.CreateLoad(addr, Reg(t.Rd),
                    IrOperand.AnnotatedMemory(0, t.Immediate, 32, "PC-relative literal"))); return;
            }

            // ── LDRD/STRD (double-word) ──
            case Thumb2Opcode.LDRD_IMM:
                Emit(IrInstruction.CreateLoad(addr, Reg(t.Rd), Mem(t.Rn, t.Immediate, 32)));
                Emit(IrInstruction.CreateLoad(addr + 1, Reg(t.Rm), Mem(t.Rn, t.Immediate + 4, 32))); return;
            case Thumb2Opcode.STRD_IMM:
                Emit(IrInstruction.CreateStore(addr, Mem(t.Rn, t.Immediate, 32), DataReg(t.Rd)));
                Emit(IrInstruction.CreateStore(addr + 1, Mem(t.Rn, t.Immediate + 4, 32), DataReg(t.Rm))); return;

            // ── Comparison ──
            case Thumb2Opcode.CMP_IMM:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Compare, Sources = [Reg(t.Rn), Imm(t.Immediate)] }); return;
            case Thumb2Opcode.CMP_REG:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Compare, Sources = [Reg(t.Rn), DataReg(t.Rm)] }); return;
            case Thumb2Opcode.TST_IMM:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Test, Sources = [Reg(t.Rn), Imm(t.Immediate)] }); return;
            case Thumb2Opcode.TST_REG:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Test, Sources = [Reg(t.Rn), DataReg(t.Rm)] }); return;

            // ── Branches ──
            case Thumb2Opcode.B:
            {
                // Check if this is a tail call
                var bResolved = _callResolver.TryResolve((ulong)t.Immediate);
                if (bResolved != null && !bResolved.IsRuntimeHelper && bResolved.DeclaringType != null)
                {
                    // Tail call
                    string bAnnotation = bResolved.FullName;
                    if (bResolved.IsStatic) bAnnotation = "static " + bAnnotation;
                    if (bResolved.MethodIndex >= 0) bAnnotation = $"[M:{bResolved.MethodIndex}] " + bAnnotation;
                    var bCallTarget = IrOperand.CallTarget((ulong)t.Immediate, bAnnotation);

                    int bGpParams = bResolved.ParameterCount - bResolved.FpParamCount;
                    int bGpUserArgs = bGpParams + (bResolved.IsStatic ? 0 : 1);
                    int bMethodInfoIdx = bGpUserArgs;
                    if (bGpUserArgs > 4) bGpUserArgs = 4;
                    int bFpArgs = Math.Min(bResolved.FpArgCount, 8);

                    int bTotal = 1 + bGpUserArgs + bFpArgs + 1; // Always append MethodInfo*
                    var bCallSources = new IrOperand[bTotal];
                    bCallSources[0] = bCallTarget;
                    for (int a = 0; a < bGpUserArgs; a++)
                        bCallSources[1 + a] = Reg(a);
                    for (int f = 0; f < bFpArgs; f++)
                        bCallSources[1 + bGpUserArgs + f] = FpReg(f, bResolved.HasDoubleArgs);
                    
                    bCallSources[bTotal - 1] = bMethodInfoIdx < 4
                        ? Reg(bMethodInfoIdx)
                        : IrOperand.NamedRegister(-1, 32, "MethodInfo");

                    Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.TailCall, Sources = bCallSources, Annotation = bAnnotation });
                    return;
                }

                var brInst = IrInstruction.CreateBranch(addr, IrOperand.Label((ulong)t.Immediate));
                if (bResolved?.FullName != null) brInst.Annotation = bResolved.FullName;
                Emit(brInst); return;
            }

            case Thumb2Opcode.B_COND:
                Emit(IrInstruction.CreateCondBranch(addr, IrOperand.Condition(t.Condition), IrOperand.Label((ulong)t.Immediate))); return;

            case Thumb2Opcode.CBZ:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.ConditionalBranch, Condition = IrBranchCondition.Zero, Sources = [Reg(t.Rd), IrOperand.Label((ulong)t.Immediate)], Annotation = "cbz" }); return;
            case Thumb2Opcode.CBNZ:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.ConditionalBranch, Condition = IrBranchCondition.NotZero, Sources = [Reg(t.Rd), IrOperand.Label((ulong)t.Immediate)], Annotation = "cbnz" }); return;

            case Thumb2Opcode.BX:
                if (t.Rm == 14) // BX LR = Return
                {
                    IrOperand? retVal = null;
                    if (_currentReturnType != "void")
                    {
                        bool isFpReturn = IsFloatReturnType(_currentReturnType);
                        retVal = isFpReturn ? FpReg(0, false) : Reg(0);
                    }
                    Emit(IrInstruction.CreateReturn(addr, retVal));
                }
                else
                {
                    Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.IndirectBranch, Sources = [Reg(t.Rm)] });
                }
                return;

            // ── BLX <reg> — register-indirect call ──
            case Thumb2Opcode.BLX_REG:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.IndirectCall, Destination = Reg(0), Sources = [Reg(t.Rm)], ClobberedRegisters = IrInstruction.Thumb2CallerSavedRegisters }); return;

            // ── BL — direct call ──
            case Thumb2Opcode.BL:
            case Thumb2Opcode.BLX:
            {
                var resolved = _callResolver.TryResolve((ulong)t.Immediate);
                string? annotation = resolved?.FullName;
                if (resolved != null && resolved.IsStatic && annotation != null)
                    annotation = "static " + annotation;
                if (resolved != null && resolved.MethodIndex >= 0 && annotation != null)
                    annotation = $"[M:{resolved.MethodIndex}] " + annotation;
                var callTarget = IrOperand.CallTarget((ulong)t.Immediate, annotation ?? $"func_{(ulong)t.Immediate:X}");

                // AAPCS: R0-R3 for GP args, S0-S15 for FP args
                int gpUserArgs = 0;
                int fpArgCount = 0;
                bool fpArgsAreDouble = false;
                int methodInfoGpIdx = -1;
                bool hasMethodInfo = false;
                if (resolved != null && !resolved.IsRuntimeHelper)
                {
                    // FpParamCount = logical params using FP regs (not physical reg count)
                    // e.g., Vector3 uses 3 FP regs but counts as 1 FP param
                    int gpParams = resolved.ParameterCount - resolved.FpParamCount;
                    gpUserArgs = gpParams + (resolved.IsStatic ? 0 : 1);
                    methodInfoGpIdx = gpUserArgs;
                    if (gpUserArgs > 4) gpUserArgs = 4; // R0-R3 only
                    if (gpUserArgs < 0) gpUserArgs = 0; // Guard against HFA overflow

                    fpArgCount = resolved.FpArgCount;
                    fpArgsAreDouble = resolved.HasDoubleArgs;
                    if (fpArgCount > 8) fpArgCount = 8;
                    
                    hasMethodInfo = true;
                }

                // C math functions
                if (resolved != null && resolved.IsRuntimeHelper && resolved.MethodName != null)
                {
                    fpArgCount = GetMathFpArgCount(resolved.MethodName);
                    fpArgsAreDouble = IsMathDoubleVariant(resolved.MethodName);
                }

                int totalSources = 1 + gpUserArgs + fpArgCount + (hasMethodInfo ? 1 : 0);
                var callSources = new IrOperand[totalSources];
                callSources[0] = callTarget;
                for (int a = 0; a < gpUserArgs; a++)
                    callSources[1 + a] = Reg(a);
                for (int f = 0; f < fpArgCount; f++)
                    callSources[1 + gpUserArgs + f] = FpReg(f, fpArgsAreDouble);
                if (hasMethodInfo)
                {
                    callSources[totalSources - 1] = methodInfoGpIdx < 4
                        ? Reg(methodInfoGpIdx)
                        : IrOperand.NamedRegister(-1, 32, "MethodInfo");
                }

                // Return type resolution
                string? resultType = null;
                if (resolved != null && !resolved.IsVoid && !resolved.IsRuntimeHelper && resolved.MethodIndex >= 0)
                    resultType = _callResolver.ResolveReturnType(resolved.MethodIndex);

                if (resolved != null && resolved.IsRuntimeHelper && resolved.MethodName != null)
                {
                    string? csharpName = MapCMathToCSharp(resolved.MethodName);
                    if (csharpName != null)
                    {
                        annotation = $"static {csharpName}";
                        resultType ??= fpArgsAreDouble ? "System.Double" : "System.Single";
                    }
                }

                IrOperand? callDst;
                if (resolved != null && resolved.IsVoid)
                    callDst = null;
                else if (IsFloatReturnType(resultType))
                    callDst = FpReg(0, resultType == "System.Double" || resultType == "double");
                else
                    callDst = Reg(0);

                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Call, Destination = callDst, Sources = callSources, Annotation = annotation, ResultType = resultType }); return;
            }

            // ── VFP Floating Point ──
            case Thumb2Opcode.VLDR:
            {
                byte sw = t.Shift; // 32 or 64
                Emit(IrInstruction.CreateLoad(addr, IrOperand.FpRegister(t.Rd, sw), Mem(t.Rn, t.Immediate, sw))); return;
            }
            case Thumb2Opcode.VSTR:
            {
                byte sw = t.Shift;
                Emit(IrInstruction.CreateStore(addr, Mem(t.Rn, t.Immediate, sw), IrOperand.FpRegister(t.Rd, sw))); return;
            }
            case Thumb2Opcode.VMOV_REG:
            {
                byte sw = t.Shift;
                Emit(IrInstruction.CreateAssign(addr, IrOperand.FpRegister(t.Rd, sw), IrOperand.FpRegister(t.Rm, sw))); return;
            }
            case Thumb2Opcode.VMOV_GP_TO_FP:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Bitcast, Destination = IrOperand.FpRegister(t.Rd, 32), Sources = [Reg(t.Rn)] }); return;
            case Thumb2Opcode.VMOV_FP_TO_GP:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Bitcast, Destination = Reg(t.Rd), Sources = [IrOperand.FpRegister(t.Rn, 32)] }); return;
            case Thumb2Opcode.VADD:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FAdd, FpReg(t.Rd, t.Shift >= 64), FpReg(t.Rn, t.Shift >= 64), FpReg(t.Rm, t.Shift >= 64))); return;
            case Thumb2Opcode.VSUB:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FSub, FpReg(t.Rd, t.Shift >= 64), FpReg(t.Rn, t.Shift >= 64), FpReg(t.Rm, t.Shift >= 64))); return;
            case Thumb2Opcode.VMUL:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FMul, FpReg(t.Rd, t.Shift >= 64), FpReg(t.Rn, t.Shift >= 64), FpReg(t.Rm, t.Shift >= 64))); return;
            case Thumb2Opcode.VDIV:
                Emit(IrInstruction.CreateBinOp(addr, IrOpcode.FDiv, FpReg(t.Rd, t.Shift >= 64), FpReg(t.Rn, t.Shift >= 64), FpReg(t.Rm, t.Shift >= 64))); return;
            case Thumb2Opcode.VNEG:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FNeg, Destination = FpReg(t.Rd, t.Shift >= 64), Sources = [FpReg(t.Rm, t.Shift >= 64)] }); return;
            case Thumb2Opcode.VABS:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FAbs, Destination = FpReg(t.Rd, t.Shift >= 64), Sources = [FpReg(t.Rm, t.Shift >= 64)] }); return;
            case Thumb2Opcode.VSQRT:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FSqrt, Destination = FpReg(t.Rd, t.Shift >= 64), Sources = [FpReg(t.Rm, t.Shift >= 64)] }); return;
            case Thumb2Opcode.VCMP:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FCompare, Sources = [FpReg(t.Rn, t.Shift >= 64), FpReg(t.Rm, t.Shift >= 64)] }); return;
            case Thumb2Opcode.VMRS:
                return; // VMRS APSR_nzcv, FPSCR — flag transfer, no IR needed (flags are implicit)
            case Thumb2Opcode.VCVT_F32_S32:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.SignedIntToFloat, Destination = FpReg(t.Rd, false), Sources = [FpReg(t.Rm, false)] }); return;
            case Thumb2Opcode.VCVT_S32_F32:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FloatToSignedInt, Destination = FpReg(t.Rd, false), Sources = [FpReg(t.Rm, false)] }); return;
            case Thumb2Opcode.VCVT_F32_U32:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.UnsignedIntToFloat, Destination = FpReg(t.Rd, false), Sources = [FpReg(t.Rm, false)] }); return;
            case Thumb2Opcode.VCVT_U32_F32:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FloatToUnsignedInt, Destination = FpReg(t.Rd, false), Sources = [FpReg(t.Rm, false)] }); return;
            case Thumb2Opcode.VCVT_F64_F32:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FloatExtend, Destination = FpReg(t.Rd, true), Sources = [FpReg(t.Rm, false)] }); return;
            case Thumb2Opcode.VCVT_F32_F64:
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.FloatTruncate, Destination = FpReg(t.Rd, false), Sources = [FpReg(t.Rm, true)] }); return;

            default:
                Console.WriteLine($"[LIFTER-UNKNOWN-THUMB2] Unknown instruction at 0x{addr:X}: {t}");
                Emit(new IrInstruction { Address = addr, Opcode = IrOpcode.Unknown, Annotation = t.ToString() }); return;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>R13 = SP, R14 = LR, R15 = PC. All 32-bit, named R0-R15.</summary>
    private IrOperand Reg(int regNum) => regNum switch
    {
        13 => IrOperand.NamedRegister(13, 32, "SP"),
        14 => IrOperand.NamedRegister(14, 32, "LR"),
        15 => IrOperand.NamedRegister(15, 32, "PC"),
        _ => IrOperand.NamedRegister(regNum, 32, $"R{regNum}")
    };
    /// <summary>For data sources: no "zero register" in ARM32 (unlike XZR in ARM64).</summary>
    private IrOperand DataReg(int regNum) => Reg(regNum);
    private IrOperand FpReg(int regNum, bool isDouble) => IrOperand.FpRegister(regNum, (byte)(isDouble ? 64 : 32));
    private string RegStr(int regNum) => regNum == 13 ? "SP" : regNum == 14 ? "LR" : regNum == 15 ? "PC" : $"R{regNum}";
    private IrOperand Imm(long val) => IrOperand.Immediate(val, 32);
    private IrOperand Mem(int baseReg, long offset, byte width)
    {
        string baseName = baseReg switch { 13 => "SP", 14 => "LR", 15 => "PC", _ => $"R{baseReg}" };
        return IrOperand.AnnotatedMemory(baseReg, offset, width, baseName);
    }

    private static bool WritesToRd(in Thumb2Instruction inst) => inst.Opcode switch
    {
        Thumb2Opcode.MOV_IMM or Thumb2Opcode.MOV_REG or Thumb2Opcode.MOVW or Thumb2Opcode.MOVT => true,
        Thumb2Opcode.MVN_IMM or Thumb2Opcode.MVN_REG or Thumb2Opcode.NEG => true,
        Thumb2Opcode.ADD_IMM or Thumb2Opcode.ADD_REG or Thumb2Opcode.SUB_IMM or Thumb2Opcode.SUB_REG => true,
        Thumb2Opcode.ADC_IMM or Thumb2Opcode.ADC_REG or Thumb2Opcode.SBC_IMM or Thumb2Opcode.SBC_REG => true,
        Thumb2Opcode.MUL or Thumb2Opcode.MLA or Thumb2Opcode.SDIV or Thumb2Opcode.UDIV => true,
        Thumb2Opcode.AND_IMM or Thumb2Opcode.AND_REG or Thumb2Opcode.ORR_IMM or Thumb2Opcode.ORR_REG => true,
        Thumb2Opcode.EOR_IMM or Thumb2Opcode.EOR_REG or Thumb2Opcode.BIC_IMM or Thumb2Opcode.BIC_REG => true,
        Thumb2Opcode.LSL_IMM or Thumb2Opcode.LSL_REG or Thumb2Opcode.LSR_IMM or Thumb2Opcode.LSR_REG => true,
        Thumb2Opcode.ASR_IMM or Thumb2Opcode.ASR_REG => true,
        Thumb2Opcode.LDR_IMM or Thumb2Opcode.LDR_REG or Thumb2Opcode.LDR_LIT => true,
        Thumb2Opcode.LDRB_IMM or Thumb2Opcode.LDRH_IMM or Thumb2Opcode.LDRSB_IMM or Thumb2Opcode.LDRSH_IMM => true,
        Thumb2Opcode.LDRD_IMM => true,
        _ => false,
    };

    // ── C math function support (shared with ARM64 lifter) ──
    private static readonly HashSet<string> _floatReturningFunctions = new(StringComparer.Ordinal)
    {
        "sinf", "cosf", "tanf", "sqrtf", "fabsf", "floorf", "ceilf", "roundf",
        "fmodf", "powf", "expf", "logf", "log2f", "log10f", "atan2f", "asinf", "acosf", "atanf",
        "sin", "cos", "tan", "sqrt", "fabs", "floor", "ceil", "round",
        "fmod", "pow", "exp", "log", "log2", "log10", "atan2", "asin", "acos", "atan",
    };

    private static bool IsFloatReturnType(string? resultType)
    {
        if (resultType is "System.Single" or "System.Double" or "float" or "double" or "Single" or "Double")
            return true;
        return false;
    }

    private static readonly Dictionary<string, int> _mathFpArgCounts = new(StringComparer.Ordinal)
    {
        ["sinf"] = 1, ["cosf"] = 1, ["tanf"] = 1, ["sqrtf"] = 1,
        ["fabsf"] = 1, ["floorf"] = 1, ["ceilf"] = 1, ["roundf"] = 1,
        ["expf"] = 1, ["logf"] = 1, ["log2f"] = 1, ["log10f"] = 1,
        ["asinf"] = 1, ["acosf"] = 1, ["atanf"] = 1,
        ["fmodf"] = 2, ["powf"] = 2, ["atan2f"] = 2,
        ["sin"] = 1, ["cos"] = 1, ["tan"] = 1, ["sqrt"] = 1,
        ["fabs"] = 1, ["floor"] = 1, ["ceil"] = 1, ["round"] = 1,
        ["exp"] = 1, ["log"] = 1, ["log2"] = 1, ["log10"] = 1,
        ["asin"] = 1, ["acos"] = 1, ["atan"] = 1,
        ["fmod"] = 2, ["pow"] = 2, ["atan2"] = 2,
    };

    private static int GetMathFpArgCount(string funcName) =>
        _mathFpArgCounts.TryGetValue(funcName, out int count) ? count : 0;

    private static bool IsMathDoubleVariant(string funcName) =>
        _mathFpArgCounts.ContainsKey(funcName) && !funcName.EndsWith('f');

    private static string? MapCMathToCSharp(string funcName)
    {
        if (!_mathFpArgCounts.ContainsKey(funcName)) return null;
        if (funcName == "fabsf") return "Mathf.Abs";
        if (funcName == "fabs") return "Math.Abs";
        if (funcName == "fmodf") return "Mathf.Repeat";
        if (funcName == "fmod") return "Math.IEEERemainder";
        if (funcName == "ceil") return "Math.Ceiling";
        bool isFloat = funcName.EndsWith('f');
        string baseName = isFloat ? funcName[..^1] : funcName;
        if (baseName.Length > 0)
            baseName = char.ToUpperInvariant(baseName[0]) + baseName[1..];
        return isFloat ? $"Mathf.{baseName}" : $"Math.{baseName}";
    }
}
