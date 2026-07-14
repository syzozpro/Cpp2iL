using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Analysis.Resolve;
using Rosetta.Binary;
using Rosetta.Lifter.ClangRules;
using Rosetta.Lifter.Models;
using Rosetta.Metadata;
using Rosetta.Pipeline;
using Iced.Intel;

namespace Rosetta.Lifter.Disassembly;

/// <summary>
/// Decodes ARM64 method bodies into raw disassembly with call annotations
/// and ADRP+LDR address annotations showing what each memory access means.
/// </summary>
public sealed class MethodDisassembler
{
    private readonly ReadOnlyMemory<byte> _binaryData;
    private readonly IBinaryParser _elf;
    private readonly MetadataParser _metadata;
    private readonly CallResolver _callResolver;
    private readonly GlobalAddressMap? _addressMap;
    private readonly ulong[] _sortedMethodVAs;

    public MethodDisassembler(
        ReadOnlyMemory<byte> binaryData,
        IBinaryParser elf,
        MetadataParser metadata,
        CallResolver callResolver,
        Dictionary<int, ulong> methodAddressMap,
        GlobalAddressMap? addressMap = null)
    {
        _binaryData = binaryData;
        _elf = elf;
        _metadata = metadata;
        _callResolver = callResolver;
        _addressMap = addressMap;

        // Build sorted method VA list for boundary detection
        _sortedMethodVAs = methodAddressMap.Values
            .Where(v => v != 0)
            .Distinct()
            .OrderBy(v => v)
            .ToArray();
    }

    public ulong GetMethodEndVA(ulong startVA)
    {
        int idx = Array.BinarySearch(_sortedMethodVAs, startVA);
        if (idx >= 0)
        {
            if (idx + 1 < _sortedMethodVAs.Length)
                return _sortedMethodVAs[idx + 1];
            return 0; // last method
        }

        int insertionPoint = ~idx;
        if (insertionPoint < _sortedMethodVAs.Length)
            return _sortedMethodVAs[insertionPoint];
        return 0;
    }

    /// <summary>
    /// Decode a method body to raw ARM64 instructions (no formatting).
    /// This is the entry point for CFG analysis — returns the instruction array
    /// and the method's end VA for boundary detection.
    /// </summary>
    public (Arm64Instruction[] Instructions, ulong EndVA)? DecodeInstructions(ulong methodVA)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"MethodDisassembler.DecodeInstructions(0x{methodVA:X})");
        ulong endVA = GetMethodEndVA(methodVA);
        long fileOffset = _elf.VirtualToFileOffset(methodVA);
        if (fileOffset < 0) return null;

        int maxBytes;
        if (endVA > methodVA)
            maxBytes = Math.Min((int)(endVA - methodVA), _binaryData.Length - (int)fileOffset);
        else
            maxBytes = Math.Min(_binaryData.Length - (int)fileOffset, 4096 * 4);

        if (maxBytes <= 0) return null;

        var instructions = Arm64Decoder.DecodeBlock(
            _binaryData.Span.Slice((int)fileOffset, maxBytes), methodVA, maxBytes / 4);

        // Trim to method boundary (stop at endVA)
        // Note: we do NOT stop at the first RET because IL2CPP methods contain
        // throw handler blocks AFTER the main RET (e.g., NullCheck throw blocks,
        // bounds check handlers). These must be included in the CFG.
        // Source: il2cpp-codegen.h L631-637 — NullCheck is inline and the throw
        //         path is placed after the normal return.
        int count = instructions.Length;
        if (endVA > methodVA)
        {
            for (int i = 0; i < instructions.Length; i++)
            {
                if (instructions[i].Address >= endVA)
                {
                    count = i;
                    break;
                }
            }
        }

        if (count < instructions.Length)
            instructions = instructions[..count];

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  MethodDisassembler: decoded {instructions.Length} instructions (endVA=0x{endVA:X})");
        return (instructions, endVA);
    }

    /// <summary>
    /// Decode a method body to raw Thumb2 instructions (ARM32).
    /// Equivalent of DecodeInstructions for armeabi-v7a binaries.
    /// </summary>
    public (Thumb2Instruction[] Instructions, ulong EndVA)? DecodeInstructionsThumb2(ulong methodVA)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"MethodDisassembler.DecodeInstructionsThumb2(0x{methodVA:X})");
        ulong endVA = GetMethodEndVA(methodVA);
        long fileOffset = _elf.VirtualToFileOffset(methodVA);
        if (fileOffset < 0) return null;

        int maxBytes;
        if (endVA > methodVA)
            maxBytes = Math.Min((int)(endVA - methodVA), _binaryData.Length - (int)fileOffset);
        else
            maxBytes = Math.Min(_binaryData.Length - (int)fileOffset, 4096 * 4);

        if (maxBytes <= 0) return null;

        var instructions = Thumb2MethodDecoder.DecodeBlock(
            _binaryData.Span.Slice((int)fileOffset, maxBytes), methodVA, maxBytes / 2);

        // Trim to method boundary
        int count = instructions.Length;
        if (endVA > methodVA)
        {
            for (int i = 0; i < instructions.Length; i++)
            {
                if (instructions[i].Address >= endVA)
                {
                    count = i;
                    break;
                }
            }
        }

        if (count < instructions.Length)
            instructions = instructions[..count];

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  MethodDisassembler: decoded {instructions.Length} Thumb2 instructions (endVA=0x{endVA:X})");
        return (instructions, endVA);
    }

    /// <summary>
    /// Decode ARM32 ARM-mode method bodies (4-byte fixed-width instructions).
    /// Uses Capstone in ARM mode, then maps each instruction to a Thumb2Instruction
    /// so the rest of the pipeline (lifter, CFG, SSA) works unchanged.
    /// </summary>
    public (Thumb2Instruction[] Instructions, ulong EndVA)? DecodeInstructionsArm32(ulong methodVA)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"MethodDisassembler.DecodeInstructionsArm32(0x{methodVA:X})");
        ulong endVA = GetMethodEndVA(methodVA);
        long fileOffset = _elf.VirtualToFileOffset(methodVA);
        if (fileOffset < 0) return null;

        int maxBytes;
        if (endVA > methodVA)
            maxBytes = Math.Min((int)(endVA - methodVA), _binaryData.Length - (int)fileOffset);
        else
            maxBytes = Math.Min(_binaryData.Length - (int)fileOffset, 4096 * 4);

        if (maxBytes <= 0) return null;

        // Disassemble with Capstone in ARM mode
        byte[] codeBytes = _binaryData.Span.Slice((int)fileOffset, maxBytes).ToArray();
        using var disasm = Gee.External.Capstone.CapstoneDisassembler.CreateArmDisassembler(
            Gee.External.Capstone.Arm.ArmDisassembleMode.Arm);
        disasm.EnableInstructionDetails = true;

        var csInstructions = disasm.Disassemble(codeBytes, (long)methodVA);

        // Convert Capstone ARM instructions to Thumb2Instruction[]
        var result = new List<Thumb2Instruction>(csInstructions.Length);
        foreach (var csInst in csInstructions)
        {
            ulong addr = (ulong)csInst.Address;
            if (endVA > methodVA && addr >= endVA) break;

            var t2 = CapstoneArmToThumb2(csInst, addr);
            result.Add(t2);
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  MethodDisassembler: decoded {result.Count} ARM32 (ARM mode) instructions (endVA=0x{endVA:X})");
        return (result.ToArray(), endVA);
    }

    /// <summary>
    /// Map a Capstone ARM-mode instruction to a Thumb2Instruction.
    /// Maps mnemonic + structured operands to the Thumb2Opcode enum and fields.
    /// </summary>
    private static Thumb2Instruction CapstoneArmToThumb2(
        Gee.External.Capstone.Arm.ArmInstruction csInst, ulong addr)
    {
        string mnemonic = csInst.Mnemonic ?? "";
        var details = csInst.Details;
        var ops = details?.Operands ?? Array.Empty<Gee.External.Capstone.Arm.ArmOperand>();

        // Read raw 4-byte value from instruction bytes for RawValue field
        uint raw = 0;
        // ARM instructions are always 4 bytes
        // We don't have the raw bytes here directly, but we can reconstruct from address

        // Helper: extract register index (0-15) from Capstone ArmRegister
        static byte RegIdx(Gee.External.Capstone.Arm.ArmRegister? reg)
        {
            if (reg == null) return 0;
            string name = reg.Name?.ToLowerInvariant() ?? "";
            if (name == "sp") return 13;
            if (name == "lr") return 14;
            if (name == "pc") return 15;
            if (name == "fp") return 11; // Frame pointer alias
            if (name == "ip") return 12; // Intra-procedure scratch alias
            if (name == "sb") return 9;  // Static base alias
            if (name == "sl") return 10; // Stack limit alias
            if (name.StartsWith("r") && int.TryParse(name.AsSpan(1), out int rn))
                return (byte)rn;
            return 0;
        }

        // Helper: get operand register
        byte GetReg(int opIdx)
        {
            if (opIdx >= ops.Length) return 0;
            var op = ops[opIdx];
            if (op.Type == Gee.External.Capstone.Arm.ArmOperandType.Register)
                return RegIdx(op.Register);
            return 0;
        }

        // Helper: get operand immediate
        long GetImm(int opIdx)
        {
            if (opIdx >= ops.Length) return 0;
            var op = ops[opIdx];
            if (op.Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                return op.Immediate;
            return 0;
        }

        // Helper: get shift from operand
        byte GetShift(int opIdx)
        {
            if (opIdx >= ops.Length) return 0;
            try { return (byte)ops[opIdx].ShiftValue; }
            catch (InvalidOperationException) { return 0; } // ShiftValue invalid when shift type is Invalid
        }

        // Helper: build register list bitmask from operands
        ushort BuildRegList()
        {
            ushort mask = 0;
            foreach (var op in ops)
            {
                if (op.Type == Gee.External.Capstone.Arm.ArmOperandType.Register)
                    mask |= (ushort)(1 << RegIdx(op.Register));
            }
            return mask;
        }

        // Helper: get condition code
        byte GetCond()
        {
            if (details == null) return 14; // AL
            int cc = (int)details.ConditionCode;
            // Capstone ARM condition codes: 0=Invalid, 1=EQ, 2=NE, ... maps to ARM cond-1
            // But we treat Invalid/AL as 14
            if (cc <= 0 || cc > 15) return 14;
            return (byte)(cc - 1);
        }

        // Strip condition suffix from mnemonic for matching (e.g., "movwne" -> "movw")
        string baseMnemonic = mnemonic;
        byte cond = GetCond();
        bool isConditional = cond != 14;

        // ARM conditional instructions: Capstone emits e.g. "movwne", "movne", "strbne".
        // Strip the 2-char condition suffix so the switch matches the base opcode.
        // Must NOT strip branch mnemonics (b, bl, blx, bx) — those are handled explicitly below.
        if (isConditional && baseMnemonic.Length > 2 &&
            !baseMnemonic.StartsWith("b")) // branches handled separately
        {
            baseMnemonic = baseMnemonic[..^2];
        }

        // Match by mnemonic
        switch (baseMnemonic)
        {
            case "push":
                return new(raw, addr, Thumb2Opcode.PUSH, size: 4, registerList: BuildRegList());

            case "pop":
                return new(raw, addr, Thumb2Opcode.POP, size: 4, registerList: BuildRegList());

            case "mov":
            case "movs":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.MOV_IMM, rd: GetReg(0), immediate: GetImm(1), size: 4);
                return new(raw, addr, Thumb2Opcode.MOV_REG, rd: GetReg(0), rm: GetReg(1), size: 4);

            case "movw":
                return new(raw, addr, Thumb2Opcode.MOVW, rd: GetReg(0), immediate: GetImm(1), size: 4);

            case "movt":
                return new(raw, addr, Thumb2Opcode.MOVT, rd: GetReg(0), immediate: GetImm(1), size: 4);

            case "mvn":
            case "mvns":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.MVN_IMM, rd: GetReg(0), immediate: GetImm(1), size: 4);
                return new(raw, addr, Thumb2Opcode.MVN_REG, rd: GetReg(0), rm: GetReg(1), size: 4);

            case "add":
            case "adds":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.ADD_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);
                if (ops.Length >= 3)
                    return new(raw, addr, Thumb2Opcode.ADD_REG, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), shift: GetShift(2), size: 4);
                if (ops.Length == 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.ADD_IMM, rd: GetReg(0), rn: GetReg(0), immediate: GetImm(1), size: 4);
                return new(raw, addr, Thumb2Opcode.ADD_REG, rd: GetReg(0), rn: GetReg(0), rm: GetReg(1), size: 4);

            case "sub":
            case "subs":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.SUB_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);
                if (ops.Length >= 3)
                    return new(raw, addr, Thumb2Opcode.SUB_REG, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), shift: GetShift(2), size: 4);
                if (ops.Length == 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.SUB_IMM, rd: GetReg(0), rn: GetReg(0), immediate: GetImm(1), size: 4);
                return new(raw, addr, Thumb2Opcode.SUB_REG, rd: GetReg(0), rn: GetReg(0), rm: GetReg(1), size: 4);

            case "adc":
            case "adcs":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.ADC_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);
                return new(raw, addr, Thumb2Opcode.ADC_REG, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), shift: GetShift(2), size: 4);

            case "sbc":
            case "sbcs":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.SBC_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);
                return new(raw, addr, Thumb2Opcode.SBC_REG, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), shift: GetShift(2), size: 4);

            case "rsb":
            case "rsbs":
                // RSB Rd, Rm, #0 is NEG
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate && GetImm(2) == 0)
                    return new(raw, addr, Thumb2Opcode.NEG, rd: GetReg(0), rm: GetReg(1), size: 4);
                // General RSB: treat as SUB with swapped operands (approximate)
                return new(raw, addr, Thumb2Opcode.SUB_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);

            case "mul":
            case "muls":
                return new(raw, addr, Thumb2Opcode.MUL, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), size: 4);

            case "mla":
                return new(raw, addr, Thumb2Opcode.MLA, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), size: 4);

            case "sdiv":
                return new(raw, addr, Thumb2Opcode.SDIV, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), size: 4);

            case "udiv":
                return new(raw, addr, Thumb2Opcode.UDIV, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), size: 4);

            case "and":
            case "ands":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.AND_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);
                return new(raw, addr, Thumb2Opcode.AND_REG, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), shift: GetShift(2), size: 4);

            case "orr":
            case "orrs":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.ORR_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);
                return new(raw, addr, Thumb2Opcode.ORR_REG, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), shift: GetShift(2), size: 4);

            case "eor":
            case "eors":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.EOR_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);
                return new(raw, addr, Thumb2Opcode.EOR_REG, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), shift: GetShift(2), size: 4);

            case "bic":
            case "bics":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.BIC_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);
                return new(raw, addr, Thumb2Opcode.BIC_REG, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), shift: GetShift(2), size: 4);

            case "lsl":
            case "lsls":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.LSL_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);
                return new(raw, addr, Thumb2Opcode.LSL_REG, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), size: 4);

            case "lsr":
            case "lsrs":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.LSR_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);
                return new(raw, addr, Thumb2Opcode.LSR_REG, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), size: 4);

            case "asr":
            case "asrs":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.ASR_IMM, rd: GetReg(0), rn: GetReg(1), immediate: GetImm(2), size: 4);
                return new(raw, addr, Thumb2Opcode.ASR_REG, rd: GetReg(0), rn: GetReg(1), rm: GetReg(2), size: 4);

            case "cmp":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.CMP_IMM, rn: GetReg(0), immediate: GetImm(1), size: 4);
                return new(raw, addr, Thumb2Opcode.CMP_REG, rn: GetReg(0), rm: GetReg(1), size: 4);

            case "cmn":
                // CMN is like ADD for flags; approximate as CMP with negated
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.CMP_IMM, rn: GetReg(0), immediate: -GetImm(1), size: 4);
                return new(raw, addr, Thumb2Opcode.CMP_REG, rn: GetReg(0), rm: GetReg(1), size: 4);

            case "tst":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Immediate)
                    return new(raw, addr, Thumb2Opcode.TST_IMM, rn: GetReg(0), immediate: GetImm(1), size: 4);
                return new(raw, addr, Thumb2Opcode.TST_REG, rn: GetReg(0), rm: GetReg(1), size: 4);

            case "ldr":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Memory)
                {
                    var mem = ops[1].Memory;
                    byte baseReg = RegIdx(mem.Base);
                    // Check for register-indexed form first: LDR Rd, [Rn, Rm]
                    // This includes LDR Rd, [PC, Rm] which is GOT-relative, NOT a literal pool load
                    if (mem.Index != null && mem.Index.Id != 0)
                        return new(raw, addr, Thumb2Opcode.LDR_REG, rd: GetReg(0), rn: baseReg, rm: RegIdx(mem.Index), size: 4);
                    if (baseReg == 15) // PC-relative displacement only → literal pool
                    {
                        long offset = mem.Displacement;
                        // ARM PC is +8 ahead, compute literal pool address
                        long target = (long)addr + 8 + offset;
                        return new(raw, addr, Thumb2Opcode.LDR_LIT, rd: GetReg(0), immediate: target, size: 4);
                    }
                    return new(raw, addr, Thumb2Opcode.LDR_IMM, rd: GetReg(0), rn: baseReg, immediate: mem.Displacement, size: 4);
                }
                break;

            case "str":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Memory)
                {
                    var mem = ops[1].Memory;
                    byte baseReg = RegIdx(mem.Base);
                    if (mem.Index != null && mem.Index.Id != 0)
                        return new(raw, addr, Thumb2Opcode.STR_REG, rd: GetReg(0), rn: baseReg, rm: RegIdx(mem.Index), size: 4);
                    return new(raw, addr, Thumb2Opcode.STR_IMM, rd: GetReg(0), rn: baseReg, immediate: mem.Displacement, size: 4);
                }
                break;

            case "ldrb":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Memory)
                {
                    var mem = ops[1].Memory;
                    return new(raw, addr, Thumb2Opcode.LDRB_IMM, rd: GetReg(0), rn: RegIdx(mem.Base), immediate: mem.Displacement, size: 4);
                }
                break;

            case "strb":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Memory)
                {
                    var mem = ops[1].Memory;
                    return new(raw, addr, Thumb2Opcode.STRB_IMM, rd: GetReg(0), rn: RegIdx(mem.Base), immediate: mem.Displacement, size: 4);
                }
                break;

            case "ldrh":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Memory)
                {
                    var mem = ops[1].Memory;
                    return new(raw, addr, Thumb2Opcode.LDRH_IMM, rd: GetReg(0), rn: RegIdx(mem.Base), immediate: mem.Displacement, size: 4);
                }
                break;

            case "strh":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Memory)
                {
                    var mem = ops[1].Memory;
                    return new(raw, addr, Thumb2Opcode.STRH_IMM, rd: GetReg(0), rn: RegIdx(mem.Base), immediate: mem.Displacement, size: 4);
                }
                break;

            case "ldrsb":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Memory)
                {
                    var mem = ops[1].Memory;
                    return new(raw, addr, Thumb2Opcode.LDRSB_IMM, rd: GetReg(0), rn: RegIdx(mem.Base), immediate: mem.Displacement, size: 4);
                }
                break;

            case "ldrsh":
                if (ops.Length >= 2 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Memory)
                {
                    var mem = ops[1].Memory;
                    return new(raw, addr, Thumb2Opcode.LDRSH_IMM, rd: GetReg(0), rn: RegIdx(mem.Base), immediate: mem.Displacement, size: 4);
                }
                break;

            case "ldrd":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Memory)
                {
                    var mem = ops[2].Memory;
                    return new(raw, addr, Thumb2Opcode.LDRD_IMM, rd: GetReg(0), rm: GetReg(1), rn: RegIdx(mem.Base), immediate: mem.Displacement, size: 4);
                }
                break;

            case "strd":
                if (ops.Length >= 3 && ops[2].Type == Gee.External.Capstone.Arm.ArmOperandType.Memory)
                {
                    var mem = ops[2].Memory;
                    return new(raw, addr, Thumb2Opcode.STRD_IMM, rd: GetReg(0), rm: GetReg(1), rn: RegIdx(mem.Base), immediate: mem.Displacement, size: 4);
                }
                break;

            case "bl":
                return new(raw, addr, Thumb2Opcode.BL, immediate: GetImm(0), size: 4);

            case "blx":
                if (ops.Length > 0 && ops[0].Type == Gee.External.Capstone.Arm.ArmOperandType.Register)
                    return new(raw, addr, Thumb2Opcode.BLX_REG, rm: GetReg(0), size: 4);
                return new(raw, addr, Thumb2Opcode.BLX, immediate: GetImm(0), size: 4);

            case "bx":
                return new(raw, addr, Thumb2Opcode.BX, rm: GetReg(0), size: 4);

            case "b":
                if (isConditional)
                    return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: cond, size: 4);
                return new(raw, addr, Thumb2Opcode.B, immediate: GetImm(0), size: 4);

            // Conditional branches: Capstone uses "beq", "bne", etc. as separate mnemonics
            case "beq": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 0, size: 4);
            case "bne": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 1, size: 4);
            case "bhs": case "bcs": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 2, size: 4);
            case "blo": case "bcc": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 3, size: 4);
            case "bmi": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 4, size: 4);
            case "bpl": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 5, size: 4);
            case "bvs": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 6, size: 4);
            case "bvc": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 7, size: 4);
            case "bhi": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 8, size: 4);
            case "bls": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 9, size: 4);
            case "bge": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 0xA, size: 4);
            case "blt": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 0xB, size: 4);
            case "bgt": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 0xC, size: 4);
            case "ble": return new(raw, addr, Thumb2Opcode.B_COND, immediate: GetImm(0), condition: 0xD, size: 4);

            // VFP
            case "vldr":
            case "vstr":
            {
                // Detect float vs double from first operand register name (s0-s31 vs d0-d31)
                byte fpBits = 32;
                byte fpReg = 0;
                if (ops.Length > 0 && ops[0].Type == Gee.External.Capstone.Arm.ArmOperandType.Register)
                {
                    string rname = ops[0].Register?.Name?.ToLowerInvariant() ?? "";
                    if (rname.StartsWith("d")) { fpBits = 64; int.TryParse(rname.AsSpan(1), out int dreg); fpReg = (byte)dreg; }
                    else if (rname.StartsWith("s")) { fpBits = 32; int.TryParse(rname.AsSpan(1), out int sreg); fpReg = (byte)sreg; }
                }
                byte baseReg = 13; // SP default
                long disp = 0;
                if (ops.Length > 1 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Memory)
                {
                    baseReg = RegIdx(ops[1].Memory.Base);
                    disp = ops[1].Memory.Displacement;
                }
                var fpOpcode = baseMnemonic == "vldr" ? Thumb2Opcode.VLDR : Thumb2Opcode.VSTR;
                return new(raw, addr, fpOpcode, rd: fpReg, rn: baseReg, immediate: disp, shift: fpBits, size: 4);
            }

            case "vadd":
            case "vsub":
            case "vmul":
            case "vdiv":
            {
                byte fpBits = 32;
                byte rd = 0, rn2 = 0, rm2 = 0;
                for (int i = 0; i < Math.Min(3, ops.Length); i++)
                {
                    if (ops[i].Type == Gee.External.Capstone.Arm.ArmOperandType.Register)
                    {
                        string rname = ops[i].Register?.Name?.ToLowerInvariant() ?? "";
                        byte regNum = 0;
                        if (rname.StartsWith("d")) { fpBits = 64; int.TryParse(rname.AsSpan(1), out int dreg); regNum = (byte)dreg; }
                        else if (rname.StartsWith("s")) { fpBits = 32; int.TryParse(rname.AsSpan(1), out int sreg); regNum = (byte)sreg; }
                        if (i == 0) rd = regNum; else if (i == 1) rn2 = regNum; else rm2 = regNum;
                    }
                }
                var fpOp = baseMnemonic switch
                {
                    "vadd" => Thumb2Opcode.VADD,
                    "vsub" => Thumb2Opcode.VSUB,
                    "vmul" => Thumb2Opcode.VMUL,
                    "vdiv" => Thumb2Opcode.VDIV,
                    _ => Thumb2Opcode.Unknown
                };
                return new(raw, addr, fpOp, rd: rd, rn: rn2, rm: rm2, shift: fpBits, size: 4);
            }

            case "vmov":
            {
                // Detect GP<->FP or FP<->FP
                if (ops.Length >= 2)
                {
                    string r0name = ops[0].Register?.Name?.ToLowerInvariant() ?? "";
                    string r1name = ops[1].Register?.Name?.ToLowerInvariant() ?? "";
                    bool r0fp = r0name.StartsWith("s") || r0name.StartsWith("d");
                    bool r1fp = r1name.StartsWith("s") || r1name.StartsWith("d");
                    if (r0fp && r1fp)
                    {
                        byte fpBits = r0name.StartsWith("d") ? (byte)64 : (byte)32;
                        byte rd2 = 0, rm2 = 0;
                        int.TryParse(r0name.AsSpan(1), out int d0); rd2 = (byte)d0;
                        int.TryParse(r1name.AsSpan(1), out int d1); rm2 = (byte)d1;
                        return new(raw, addr, Thumb2Opcode.VMOV_REG, rd: rd2, rm: rm2, shift: fpBits, size: 4);
                    }
                    if (r0fp && !r1fp)
                    {
                        int.TryParse(r0name.AsSpan(1), out int sreg);
                        return new(raw, addr, Thumb2Opcode.VMOV_GP_TO_FP, rd: (byte)sreg, rn: GetReg(1), size: 4);
                    }
                    if (!r0fp && r1fp)
                    {
                        int.TryParse(r1name.AsSpan(1), out int sreg);
                        return new(raw, addr, Thumb2Opcode.VMOV_FP_TO_GP, rd: GetReg(0), rn: (byte)sreg, size: 4);
                    }
                }
                break;
            }

            case "vcmp":
            case "vcmpe":
            {
                byte fpBits = 32;
                byte rn2 = 0, rm2 = 0;
                if (ops.Length > 0)
                {
                    string rname = ops[0].Register?.Name?.ToLowerInvariant() ?? "";
                    if (rname.StartsWith("d")) { fpBits = 64; int.TryParse(rname.AsSpan(1), out int d0); rn2 = (byte)d0; }
                    else if (rname.StartsWith("s")) { int.TryParse(rname.AsSpan(1), out int s0); rn2 = (byte)s0; }
                }
                if (ops.Length > 1 && ops[1].Type == Gee.External.Capstone.Arm.ArmOperandType.Register)
                {
                    string rname = ops[1].Register?.Name?.ToLowerInvariant() ?? "";
                    if (rname.StartsWith("d")) { int.TryParse(rname.AsSpan(1), out int d1); rm2 = (byte)d1; }
                    else if (rname.StartsWith("s")) { int.TryParse(rname.AsSpan(1), out int s1); rm2 = (byte)s1; }
                }
                return new(raw, addr, Thumb2Opcode.VCMP, rn: rn2, rm: rm2, shift: fpBits, size: 4);
            }

            case "vmrs":
                return new(raw, addr, Thumb2Opcode.VMRS, size: 4);

            case "vcvt":
            {
                // Parse from operand string for conversion direction
                string operand = csInst.Operand ?? "";
                if (operand.Contains("f32") && operand.Contains("s32"))
                    return new(raw, addr, Thumb2Opcode.VCVT_F32_S32, size: 4);
                if (operand.Contains("s32") && operand.Contains("f32"))
                    return new(raw, addr, Thumb2Opcode.VCVT_S32_F32, size: 4);
                if (operand.Contains("f64") && operand.Contains("f32"))
                    return new(raw, addr, Thumb2Opcode.VCVT_F64_F32, size: 4);
                if (operand.Contains("f32") && operand.Contains("f64"))
                    return new(raw, addr, Thumb2Opcode.VCVT_F32_F64, size: 4);
                break;
            }

            case "dmb":
                return new(raw, addr, Thumb2Opcode.DMB, size: 4);

            case "nop":
                return new(raw, addr, Thumb2Opcode.NOP, size: 4);
        }

        // Fallback: unknown instruction
        return new(raw, addr, Thumb2Opcode.Unknown, size: 4);
    }


    /// <summary>
    /// Equivalent of DecodeInstructions for x86_64 binaries using Iced.
    /// </summary>
    public (Instruction[] Instructions, ulong EndVA)? DecodeInstructionsX64(ulong methodVA)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"MethodDisassembler.DecodeInstructionsX64(0x{methodVA:X})");
        ulong endVA = GetMethodEndVA(methodVA);
        long fileOffset = _elf.VirtualToFileOffset(methodVA);
        if (fileOffset < 0) return null;

        int maxBytes;
        if (endVA > methodVA)
            maxBytes = Math.Min((int)(endVA - methodVA), _binaryData.Length - (int)fileOffset);
        else
            maxBytes = Math.Min(_binaryData.Length - (int)fileOffset, 4096 * 4); // Fallback: up to 16KB

        if (maxBytes <= 0) return null;

        var byteSpan = _binaryData.Span.Slice((int)fileOffset, maxBytes);
        var byteReader = new ByteArrayCodeReader(byteSpan.ToArray());
        var decoder = Decoder.Create(64, byteReader);
        decoder.IP = methodVA;

        var instructions = new List<Instruction>();
        while (decoder.IP < methodVA + (ulong)maxBytes)
        {
            var instr = decoder.Decode();
            if (instr.IsInvalid) break;
            instructions.Add(instr);
            
            // If we've reached the known method boundary, we can stop, but IL2CPP puts throw handlers after RET.
            // Wait, we bounded the loop by maxBytes, which is based on endVA!
            // So we don't need to break manually here.
            
            // Some padding instructions (int3 / nop) might be at the end, but they won't harm the CFG.
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  MethodDisassembler: decoded {instructions.Count} x64 instructions (endVA=0x{endVA:X})");
        return (instructions.ToArray(), endVA);
    }

    /// <summary>
    /// Decode a method body to raw ARM64 disassembly.
    /// Each line shows: [address] raw_hex  MNEMONIC operands  ; annotation
    /// BL targets are annotated with resolved method/runtime helper names.
    /// ADRP+LDR pairs are annotated with resolved metadata meanings.
    /// </summary>
    public LiftedMethod DisassembleMethod(MethodDefinition method, ulong methodVA)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"MethodDisassembler.DisassembleMethod(0x{methodVA:X})");
        int declaringTypeIdx = method.DeclaringTypeIndex;
        bool isInstance = (method.Flags & 0x0010) == 0;
        ulong endVA = GetMethodEndVA(methodVA);

        long fileOffset = _elf.VirtualToFileOffset(methodVA);
        if (fileOffset < 0)
        {
            return new LiftedMethod
            {
                MethodName = method.Name ?? $"Method_{method.GlobalIndex}",
                Lines = new List<string> { "// ERROR: could not resolve VA to file offset" },
                ParameterCount = method.ParameterCount
            };
        }

        int maxBytes;
        if (endVA > methodVA)
        {
            maxBytes = Math.Min((int)(endVA - methodVA), _binaryData.Length - (int)fileOffset);
        }
        else
        {
            maxBytes = Math.Min(_binaryData.Length - (int)fileOffset, 4096 * 4);
        }
        
        if (maxBytes <= 0)
        {
            return new LiftedMethod
            {
                MethodName = method.Name ?? $"Method_{method.GlobalIndex}",
                Lines = new List<string> { "// ERROR: no bytes to decode" },
                ParameterCount = method.ParameterCount
            };
        }

        var instructions = Arm64Decoder.DecodeBlock(
            _binaryData.Span.Slice((int)fileOffset, maxBytes), methodVA, maxBytes / 4);

        // Scan for ADRP+LDR resolved addresses
        Dictionary<int, AdrpLdrScanner.ResolvedAccess>? adrpMap = null;
        if (_addressMap != null)
        {
            var accesses = AdrpLdrScanner.Scan(instructions);
            if (accesses.Count > 0)
            {
                adrpMap = new Dictionary<int, AdrpLdrScanner.ResolvedAccess>(accesses.Count);
                foreach (var acc in accesses)
                    adrpMap[acc.InstructionIndex] = acc;
            }
        }

        var lines = new List<string>();
        var immAnnotator = new ImmediateAnnotator();

        for (int i = 0; i < instructions.Length; i++)
        {
            var inst = instructions[i];
            if (endVA > methodVA && inst.Address >= endVA) break;

            string annotation = "";

            // BL call annotation
            if (inst.Opcode == Arm64Opcode.BL)
            {
                ulong target = (ulong)inst.Immediate;
                var resolved = _callResolver.TryResolve(target);
                if (resolved != null)
                {
                    string name = resolved.MethodName ?? "";

                    // If the prober only identified it as generic "il2cpp_runtime_helper",
                    // try call-site classification for a more specific name.
                    if (name == "il2cpp_runtime_helper")
                    {
                        string? betterName = CallSiteClassifier.ClassifyFromContext(instructions, i);
                        if (betterName != null)
                            name = betterName;
                    }

                    annotation = $"  ; → {name}";
                }
            }

            // B (unconditional branch) annotation
            // Source: Labeler.cs L62 — intra-method: goto IL_XXXX;
            // Source: Clang tail-call opt — inter-method: B target replaces BL+RET
            if (inst.Opcode == Arm64Opcode.B)
            {
                annotation = BranchAnnotator.Annotate(
                    inst, methodVA, endVA, _callResolver, instructions, i);
            }

            // ADRP+LDR address annotation
            if (annotation == "" && adrpMap != null && adrpMap.TryGetValue(i, out var access))
            {
                // SIMD LDR from literal pool → read float/double from binary
                if (access.SimdSize >= 2)
                {
                    annotation = ReadLiteralPoolValue(access.TargetVA, access.SimdSize);
                }
                else
                {
                    var ann = _addressMap!.ResolveAddress(access.TargetVA, access.IsByteLdrb);
                    if (ann != null)
                    {
                        annotation = $"  ; {ann}";
                    }
                    else
                    {
                        annotation = $"  ; → [0x{access.TargetVA:X}]";
                    }
                }
            }

            // MOVZ/MOVK/MOVN immediate annotation (integers and floats)
            Arm64Instruction? nextInst = (i + 1 < instructions.Length) ? instructions[i + 1] : null;
            if (annotation == "")
            {
                var immAnn = immAnnotator.Annotate(inst, nextInst);
                if (immAnn != null)
                    annotation = $"  ; {immAnn}";
            }
            else
            {
                // Still track MOVZ state even if we already have an annotation
                immAnnotator.Annotate(inst, nextInst);
            }

            lines.Add($"[0x{inst.Address:X}] {inst.RawValue:X8}  {inst}{annotation}");

            if (inst.Opcode == Arm64Opcode.RET) break;
        }

        return new LiftedMethod
        {
            MethodName = method.Name ?? $"Method_{method.GlobalIndex}",
            DeclaringType = declaringTypeIdx >= 0 && declaringTypeIdx < _metadata.TypeDefinitions.Length
                ? _metadata.TypeDefinitions[declaringTypeIdx].FullName
                : null,
            IsStatic = !isInstance,
            Lines = lines,
            ParameterCount = method.ParameterCount
        };
    }

    /// <summary>
    /// Read a float (simdSize=2) or double (simdSize=3) from the binary at a resolved VA.
    /// Returns annotation string like "; = 3.14159f" or empty string if unreadable.
    /// </summary>
    private string ReadLiteralPoolValue(ulong va, int simdSize)
    {
        long fileOffset = _elf.VirtualToFileOffset(va);
        if (fileOffset < 0) return "";

        var span = _binaryData.Span;

        if (simdSize == 2 && fileOffset + 4 <= span.Length)
        {
            float val = BitConverter.ToSingle(span.Slice((int)fileOffset, 4));
            // Filter garbage: must be a normal finite float (not packed int data)
            if (float.IsFinite(val) && (float.IsNormal(val) || val == 0f))
                return $"  ; = {val.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}f";
        }

        if (simdSize == 3 && fileOffset + 8 <= span.Length)
        {
            double val = BitConverter.ToDouble(span.Slice((int)fileOffset, 8));
            // Filter garbage: subnormals (< 1e-300) are likely packed int pairs, not real doubles
            if (double.IsFinite(val) && (double.IsNormal(val) || val == 0.0))
                return $"  ; = {val.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}d";
        }

        return "";
    }
}
