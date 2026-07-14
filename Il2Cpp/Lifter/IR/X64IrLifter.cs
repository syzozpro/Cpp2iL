using System;
using System.Collections.Generic;
using Iced.Intel;
using Rosetta.Analysis.Resolve;
using Rosetta.Lifter.ClangRules;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Lifter.Models;
using Rosetta.Metadata;

namespace Rosetta.Lifter.IR;

public sealed class X64IrLifter
{
    private readonly CallResolver _callResolver;
    private readonly GlobalAddressMap? _addressMap;
    private string _currentReturnType = "void";
    private bool _currentIsStatic;

    public X64IrLifter(CallResolver callResolver, GlobalAddressMap? addressMap = null)
    {
        _callResolver = callResolver;
        _addressMap = addressMap;
    }

    public IrMethod Lift(MethodDefinition methodDef, ulong methodVA, Instruction[] x64Insts, string methodName, string? declaringType, string returnType, List<string> parameters, bool isStatic)
    {
        var instructions = new List<IrInstruction>(x64Insts.Length);
        _currentReturnType = returnType;
        _currentIsStatic = isStatic;

        for (int i = 0; i < x64Insts.Length; i++)
        {
            var instr = x64Insts[i];
            ulong pc = instr.IP;

            IrInstruction? irNode = null;
            switch (instr.Mnemonic)
            {
                case Mnemonic.Mov:
                case Mnemonic.Movaps:
                case Mnemonic.Movups:
                case Mnemonic.Movss:
                case Mnemonic.Movsd:
                    irNode = LiftMov(instr, pc);
                    break;
                case Mnemonic.Lea:
                    irNode = LiftLea(instr, pc);
                    break;
                case Mnemonic.Add:
                case Mnemonic.Sub:
                case Mnemonic.Mul:
                case Mnemonic.Imul:
                case Mnemonic.Div:
                case Mnemonic.Idiv:
                    irNode = LiftArithmetic(instr, pc);
                    break;
                case Mnemonic.Call:
                    irNode = LiftCall(instr, pc);
                    break;
                case Mnemonic.Ret:
                {
                    // MS x64 ABI: GP return in RAX (reg 4), FP return in XMM0 (fp reg 0)
                    IrOperand? retVal = null;
                    if (_currentReturnType != "void")
                    {
                        bool isFpReturn = _currentReturnType is "System.Single" or "System.Double" or "float" or "double";
                        retVal = isFpReturn 
                            ? IrOperand.FpRegister(0, (byte)(_currentReturnType is "System.Double" or "double" ? 64 : 32))
                            : Reg(4); // RAX = register 4 in our mapping
                    }
                    irNode = IrInstruction.CreateReturn(pc, retVal);
                    break;
                }
                case Mnemonic.Jmp:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Branch, Sources = [IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Je:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0x0), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Jne:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0x1), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Jg:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0xC), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Jge:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0xA), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Jl:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0xB), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Jle:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0xD), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Ja:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0x8), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Jae:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0x2), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Jb:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0x3), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Jbe:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0x9), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Js:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0x4), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Jns:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.ConditionalBranch, Sources = [IrOperand.Condition(0x5), IrOperand.Label(instr.NearBranchTarget)] };
                    break;
                case Mnemonic.Cmp:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Compare, Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                case Mnemonic.Test:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Test, Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                case Mnemonic.Xor:
                    if (instr.Op0Kind == OpKind.Register && instr.Op1Kind == OpKind.Register && instr.Op0Register == instr.Op1Register)
                    {
                        // xor reg, reg → standard x86 idiom for clearing register to 0
                        irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.LoadImmediate, Destination = MapReg(instr.Op0Register), Sources = [IrOperand.Immediate(0)] };
                    }
                    else
                    {
                        irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Xor, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    }
                    break;
                case Mnemonic.And:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.And, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                case Mnemonic.Or:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Or, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                case Mnemonic.Shl:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Shl, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                case Mnemonic.Shr:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Shr, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                case Mnemonic.Sar:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Sar, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                case Mnemonic.Not:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Not, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0)] };
                    break;
                case Mnemonic.Neg:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Neg, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0)] };
                    break;
                case Mnemonic.Movzx:
                case Mnemonic.Movsx:
                case Mnemonic.Movsxd:
                    irNode = LiftMov(instr, pc);
                    break;
                case Mnemonic.Nop:
                case Mnemonic.Int3:
                case Mnemonic.Endbr64:
                case Mnemonic.Cld:
                    // Skip no-ops and debug breaks
                    break;
                case Mnemonic.Push:
                    if (instr.Op0Kind == OpKind.Register)
                    {
                        // Callee-saved register save to stack
                        irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Store, Sources = [IrOperand.Memory(31, -8, 64), MapReg(instr.Op0Register)] };
                    }
                    break;
                case Mnemonic.Pop:
                    if (instr.Op0Kind == OpKind.Register)
                    {
                        // Callee-saved register restore from stack
                        irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Load, Destination = MapReg(instr.Op0Register), Sources = [IrOperand.Memory(31, 0, 64)] };
                    }
                    break;
                case Mnemonic.Xorps:
                case Mnemonic.Xorpd:
                case Mnemonic.Pxor:
                case Mnemonic.Vxorps:
                case Mnemonic.Vxorpd:
                case Mnemonic.Vpxor:
                    if (instr.Op0Kind == OpKind.Register && instr.Op1Kind == OpKind.Register && instr.Op0Register == instr.Op1Register)
                    {
                        // xorps/pxor xmm, xmm → clear float register to 0.0
                        irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.LoadImmediate, Destination = MapReg(instr.Op0Register), Sources = [IrOperand.FloatImmediate(0, 32)] };
                    }
                    else
                    {
                        irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Xor, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    }
                    break;
                case Mnemonic.Cvtss2sd:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.FloatExtend, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 1)] };
                    break;
                case Mnemonic.Cvtsd2ss:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.FloatTruncate, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 1)] };
                    break;
                case Mnemonic.Cvttss2si:
                case Mnemonic.Cvttsd2si:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.FloatToSignedInt, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 1)] };
                    break;
                case Mnemonic.Cvtsi2ss:
                case Mnemonic.Cvtsi2sd:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.SignedIntToFloat, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 1)] };
                    break;
                case Mnemonic.Addss:
                case Mnemonic.Addsd:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.FAdd, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                case Mnemonic.Subss:
                case Mnemonic.Subsd:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.FSub, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                case Mnemonic.Mulss:
                case Mnemonic.Mulsd:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.FMul, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                case Mnemonic.Divss:
                case Mnemonic.Divsd:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.FDiv, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                case Mnemonic.Comiss:
                case Mnemonic.Comisd:
                case Mnemonic.Ucomiss:
                case Mnemonic.Ucomisd:
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.FCompare, Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
                    break;
                default:
                    // Fallback to unknown
                    Console.WriteLine($"[LIFTER-UNKNOWN-X64] Unknown instruction at 0x{pc:X}: {instr}");
                    irNode = new IrInstruction { Address = pc, Opcode = IrOpcode.Unknown, Annotation = instr.ToString() };
                    break;
            }

            if (irNode != null)
                instructions.Add(irNode);
        }

        var irMethod = new IrMethod
        {
            MethodName = methodName,
            DeclaringType = declaringType,
            ReturnType = returnType,
            Parameters = parameters,
            IsStatic = isStatic,
            EntryAddress = methodVA,
            Token = methodDef.Token,
            TypeDefIndex = methodDef.DeclaringTypeIndex,
            MethodIndex = methodDef.GlobalIndex,
            Instructions = instructions,
            OriginalInstructionCount = x64Insts.Length,
            CollapsedInstructionCount = 0,
            IsArm32 = false
        };

        irMethod.BuildParamMaps();
        return irMethod;
    }

    private IrInstruction LiftMov(Instruction instr, ulong pc)
    {
        if (instr.Op0Kind == OpKind.Register && instr.Op1Kind == OpKind.Register)
        {
            return new IrInstruction { Address = pc, Opcode = IrOpcode.Assign, Destination = MapReg(instr.Op0Register), Sources = [MapReg(instr.Op1Register)] };
        }
        else if (instr.Op0Kind == OpKind.Register && (instr.Op1Kind == OpKind.Immediate8 || instr.Op1Kind == OpKind.Immediate8to16 || instr.Op1Kind == OpKind.Immediate8to32 || instr.Op1Kind == OpKind.Immediate8to64 || instr.Op1Kind == OpKind.Immediate16 || instr.Op1Kind == OpKind.Immediate32to64 || instr.Op1Kind == OpKind.Immediate32 || instr.Op1Kind == OpKind.Immediate64))
        {
            return new IrInstruction { Address = pc, Opcode = IrOpcode.LoadImmediate, Destination = MapReg(instr.Op0Register), Sources = [IrOperand.Immediate((long)instr.GetImmediate(1))] };
        }
        else if (instr.Op0Kind == OpKind.Register && instr.Op1Kind == OpKind.Memory)
        {
            if (instr.MemoryBase == Register.RIP)
            {
                ulong targetVA = instr.MemoryDisplacement64;
                string? label = null;
                IrSemanticTag tag = IrSemanticTag.None;
                int? methodIdx = null;

                var ann = _addressMap?.ResolveAddress(targetVA);
                if (ann != null && ann.Kind != AddressKind.Unknown)
                {
                    label = ann.Kind switch
                    {
                        AddressKind.StringLiteral => $"\"{ann.Label}\"",
                        AddressKind.RuntimeClass => $"typeof({ann.Label})",
                        AddressKind.RuntimeType => $"type({ann.Label})",
                        AddressKind.MethodInfo => $"MethodInfo({ann.Label})",
                        AddressKind.FieldInfo => $"field({ann.Label})",
                        AddressKind.FieldRva => $"field({ann.Label})",
                        AddressKind.MethodRef => $"MethodRef({ann.Label})",
                        _ => ann.Label,
                    };

                    tag = ann.Kind switch
                    {
                        AddressKind.MethodRef => IrSemanticTag.MethodRef,
                        AddressKind.ClassInitFlag => IrSemanticTag.ClassInit,
                        _ => IrSemanticTag.None
                    };

                    if (ann.Kind == AddressKind.MethodRef)
                        methodIdx = ann.MetadataIndex;
                }

                return new IrInstruction 
                { 
                    Address = pc, 
                    Opcode = IrOpcode.Load, 
                    Destination = MapReg(instr.Op0Register), 
                    Sources = [IrOperand.Memory(31, (long)targetVA, 64)], 
                    Annotation = label, 
                    SemanticTag = tag, 
                    TargetMethodIndex = methodIdx 
                };
            }
            
            return new IrInstruction { Address = pc, Opcode = IrOpcode.Load, Destination = MapReg(instr.Op0Register), Sources = [MapMemory(instr)] };
        }
        else if (instr.Op0Kind == OpKind.Memory && instr.Op1Kind == OpKind.Register)
        {
            var storeMem = MapMemory(instr);
            var storeInst = new IrInstruction { Address = pc, Opcode = IrOpcode.Store, Sources = [storeMem, MapReg(instr.Op1Register)] };
            if (instr.MemoryBase == Register.RIP)
            {
                var ann = _addressMap?.ResolveAddress(instr.MemoryDisplacement64);
                if (ann != null && ann.Kind != AddressKind.Unknown)
                {
                    storeInst.SemanticTag = ann.Kind == AddressKind.ClassInitFlag ? IrSemanticTag.ClassInit : IrSemanticTag.None;
                }
            }
            return storeInst;
        }
        else if (instr.Op0Kind == OpKind.Memory && (instr.Op1Kind == OpKind.Immediate8 || instr.Op1Kind == OpKind.Immediate8to16 || instr.Op1Kind == OpKind.Immediate8to32 || instr.Op1Kind == OpKind.Immediate8to64 || instr.Op1Kind == OpKind.Immediate16 || instr.Op1Kind == OpKind.Immediate32to64 || instr.Op1Kind == OpKind.Immediate32 || instr.Op1Kind == OpKind.Immediate64))
        {
            var storeMem = MapMemory(instr);
            var storeInst = new IrInstruction { Address = pc, Opcode = IrOpcode.Store, Sources = [storeMem, IrOperand.Immediate((long)instr.GetImmediate(1))] };
            if (instr.MemoryBase == Register.RIP)
            {
                var ann = _addressMap?.ResolveAddress(instr.MemoryDisplacement64);
                if (ann != null && ann.Kind != AddressKind.Unknown)
                {
                    storeInst.SemanticTag = ann.Kind == AddressKind.ClassInitFlag ? IrSemanticTag.ClassInit : IrSemanticTag.None;
                }
            }
            return storeInst;
        }
        return new IrInstruction { Address = pc, Opcode = IrOpcode.Unknown };
    }

    private IrInstruction LiftLea(Instruction instr, ulong pc)
    {
        if (instr.Op1Kind == OpKind.Memory && instr.MemoryBase == Register.RIP)
        {
            ulong targetVA = instr.MemoryDisplacement64;
            return new IrInstruction { Address = pc, Opcode = IrOpcode.LoadAddress, Destination = MapReg(instr.Op0Register), Sources = [IrOperand.Immediate((long)targetVA)] };
        }
        
        // LEA computes an address, it does NOT dereference memory.
        // lea reg, [base + disp] → reg = base + disp (address computation)
        var baseReg = instr.MemoryBase;
        long disp = (long)instr.MemoryDisplacement64;
        var indexReg = instr.MemoryIndex;
        
        if (indexReg == Register.None && baseReg != Register.None)
        {
            // Simple case: lea reg, [base + disp]
            int b = GetRegNum(baseReg);
            if (disp == 0)
            {
                // lea reg, [base] → just assign base
                return new IrInstruction { Address = pc, Opcode = IrOpcode.Assign, Destination = MapReg(instr.Op0Register), Sources = [Reg(b)] };
            }
            // lea reg, [base + disp] → reg = add base, disp
            return new IrInstruction { Address = pc, Opcode = IrOpcode.Add, Destination = MapReg(instr.Op0Register), Sources = [Reg(b), IrOperand.Immediate(disp)] };
        }
        
        if (indexReg != Register.None && baseReg != Register.None)
        {
            // Scaled index: lea reg, [base + index*scale + disp]
            int b = GetRegNum(baseReg);
            int idx = GetRegNum(indexReg);
            int scale = instr.MemoryIndexScale;
            if (scale == 1 && disp == 0)
            {
                return new IrInstruction { Address = pc, Opcode = IrOpcode.Add, Destination = MapReg(instr.Op0Register), Sources = [Reg(b), Reg(idx)] };
            }
            // For complex cases, fall through to LoadAddress
        }
        
        // Fallback: emit as LoadAddress with the computed displacement
        if (baseReg == Register.None)
        {
            return new IrInstruction { Address = pc, Opcode = IrOpcode.LoadAddress, Destination = MapReg(instr.Op0Register), Sources = [IrOperand.Immediate(disp)] };
        }
        
        // Complex LEA: emit as Add of base + displacement
        return new IrInstruction { Address = pc, Opcode = IrOpcode.Add, Destination = MapReg(instr.Op0Register), Sources = [Reg(GetRegNum(baseReg)), IrOperand.Immediate(disp)] };
    }

    private IrInstruction LiftArithmetic(Instruction instr, ulong pc)
    {
        IrOpcode op = instr.Mnemonic switch
        {
            Mnemonic.Add => IrOpcode.Add,
            Mnemonic.Sub => IrOpcode.Sub,
            Mnemonic.Mul or Mnemonic.Imul => IrOpcode.Mul,
            Mnemonic.Div or Mnemonic.Idiv => IrOpcode.SDiv,
            _ => IrOpcode.Unknown
        };

        return new IrInstruction { Address = pc, Opcode = op, Destination = MapReg(instr.Op0Register), Sources = [MapOp(instr, 0), MapOp(instr, 1)] };
    }

    private IrInstruction LiftCall(Instruction instr, ulong pc)
    {
        ulong targetVA = instr.NearBranchTarget;
        string targetName = $"sub_{targetVA:X}";
        string? annotation = targetName;
        string? resultType = null;
        
        int gpUserArgs = 0;
        int fpArgCount = 0;
        bool fpArgsAreDouble = false;

        var resolved = _callResolver.TryResolve(targetVA);
        if (resolved != null)
        {
            targetName = resolved.MethodName ?? targetName;
            annotation = resolved.FullName;
            if (resolved.IsStatic && annotation != null)
                annotation = "static " + annotation;
            
            if (!resolved.IsVoid && !resolved.IsRuntimeHelper && resolved.MethodIndex >= 0)
                resultType = _callResolver.ResolveReturnType(resolved.MethodIndex);
                
            if (!resolved.IsRuntimeHelper)
            {
                int gpParams = resolved.ParameterCount - resolved.FpParamCount;
                gpUserArgs = gpParams + (resolved.IsStatic ? 0 : 1);
                if (gpUserArgs > 4) gpUserArgs = 4;
                if (gpUserArgs < 0) gpUserArgs = 0;
                
                fpArgCount = resolved.FpArgCount;
                fpArgsAreDouble = resolved.HasDoubleArgs;
                if (fpArgCount > 4) fpArgCount = 4; // XMM0-XMM3
            }
        }

        // C math functions handling
        if (resolved != null && resolved.IsRuntimeHelper && resolved.MethodName != null)
        {
            if (resolved.MethodName.EndsWith("f")) fpArgCount = 1; // Simplistic for now
            else if (resolved.MethodName == "powf" || resolved.MethodName == "atan2f") fpArgCount = 2;
            fpArgsAreDouble = !resolved.MethodName.EndsWith("f");
        }

        IrOperand? callDst;
        if (resolved != null && resolved.IsVoid)
            callDst = null;
        else if (resultType == "System.Single" || resultType == "System.Double")
            callDst = IrOperand.FpRegister(0, (byte)(resultType == "System.Double" ? 64 : 32));
        else
            callDst = Reg(4); // MS ABI Return is RAX (4)

        int totalSources = 1 + gpUserArgs + fpArgCount;
        var callSources = new IrOperand[totalSources];
        callSources[0] = IrOperand.CallTarget(targetVA, targetName);
        
        // In MS ABI, arg0->RCX(0), arg1->RDX(1), arg2->R8(2), arg3->R9(3)
        for (int a = 0; a < gpUserArgs; a++)
            callSources[1 + a] = Reg(a);
            
        // Float args in MS ABI: XMM0(0), XMM1(1), etc.
        for (int f = 0; f < fpArgCount; f++)
            callSources[1 + gpUserArgs + f] = IrOperand.FpRegister(f, (byte)(fpArgsAreDouble ? 64 : 32));

        return new IrInstruction 
        { 
            Address = pc, 
            Opcode = IrOpcode.Call, 
            Destination = callDst,
            Sources = callSources,
            Annotation = annotation,
            ResultType = resultType,
            ClobberedRegisters = callDst == null 
                ? X64CallerSavedRegisters 
                : X64CallerSavedRegistersNoReturn
        };
    }

    /// <summary>MS x64 ABI: volatile registers clobbered by calls.
    /// RAX(4), RCX(0), RDX(1), R8(2), R9(3), R10(7), R11(8), XMM0-XMM5(100-105).
    /// Note: RSI(5), RDI(6), RBX(13), RBP(29), R12-R15(9-12) are callee-saved.</summary>
    private static readonly IReadOnlySet<int> X64CallerSavedRegisters =
        new HashSet<int> { 0, 1, 2, 3, 4, 7, 8, 100, 101, 102, 103, 104, 105 };
    /// <summary>Same as above but excluding RAX (4) for void calls.</summary>
    private static readonly IReadOnlySet<int> X64CallerSavedRegistersNoReturn =
        new HashSet<int> { 0, 1, 2, 3, 7, 8, 100, 101, 102, 103, 104, 105 };

    private static IrOperand MapOp(Instruction instr, int op)
    {
        var kind = instr.GetOpKind(op);
        if (kind == OpKind.Register) return MapReg(instr.GetOpRegister(op));
        if (kind == OpKind.Immediate8 || kind == OpKind.Immediate8to16 || kind == OpKind.Immediate8to32 || kind == OpKind.Immediate8to64 || kind == OpKind.Immediate16 || kind == OpKind.Immediate32to64 || kind == OpKind.Immediate32 || kind == OpKind.Immediate64) 
            return IrOperand.Immediate((long)instr.GetImmediate(op));
        if (kind == OpKind.Memory) return MapMemory(instr);
        return IrOperand.Immediate(0);
    }

    private static IrOperand MapMemory(Instruction instr)
    {
        var baseReg = instr.MemoryBase;
        if (baseReg == Register.RIP)
        {
            // RIP-relative: MemoryDisplacement64 is the resolved absolute VA.
            // Emit as absolute memory access with no base register (31 = SP/none).
            return IrOperand.Memory(31, (long)instr.MemoryDisplacement64, 64);
        }
        var disp = instr.MemoryDisplacement64;
        int b = baseReg != Register.None ? GetRegNum(baseReg) : 31;
        return IrOperand.Memory(b, (long)disp, 64);
    }

    private static IrOperand MapReg(Register reg)
    {
        int num = GetRegNum(reg);
        if (num >= 100) return IrOperand.FpRegister(num - 100, 64);
        return Reg(num);
    }
    
    private static IrOperand Reg(int num)
    {
        return IrOperand.Register(num, true);
    }
    
    private static int GetRegNum(Register reg)
    {
        return reg switch
        {
            // MS ABI: RCX, RDX, R8, R9 -> x0, x1, x2, x3
            Register.RCX or Register.ECX or Register.CX or Register.CL => 0,
            Register.RDX or Register.EDX or Register.DX or Register.DL => 1,
            Register.R8 or Register.R8D or Register.R8W or Register.R8L => 2,
            Register.R9 or Register.R9D or Register.R9W or Register.R9L => 3,
            
            Register.RAX or Register.EAX or Register.AX or Register.AL => 4,
            
            Register.RSI or Register.ESI or Register.SI or Register.SIL => 5,
            Register.RDI or Register.EDI or Register.DI or Register.DIL => 6,
            Register.R10 or Register.R10D or Register.R10W or Register.R10L => 7,
            Register.R11 or Register.R11D or Register.R11W or Register.R11L => 8,
            Register.R12 or Register.R12D or Register.R12W or Register.R12L => 9,
            Register.R13 or Register.R13D or Register.R13W or Register.R13L => 10,
            Register.R14 or Register.R14D or Register.R14W or Register.R14L => 11,
            Register.R15 or Register.R15D or Register.R15W or Register.R15L => 12,
            Register.RBX or Register.EBX or Register.BX or Register.BL => 13,
            Register.RBP or Register.EBP or Register.BP or Register.BPL => 29, // frame pointer
            Register.RSP or Register.ESP or Register.SP or Register.SPL => 31,

            // Float MS ABI: XMM0-XMM3 -> d0-d3 (add 100 to differentiate FP)
            Register.XMM0 or Register.YMM0 or Register.ZMM0 => 100,
            Register.XMM1 or Register.YMM1 or Register.ZMM1 => 101,
            Register.XMM2 or Register.YMM2 or Register.ZMM2 => 102,
            Register.XMM3 or Register.YMM3 or Register.ZMM3 => 103,
            Register.XMM4 or Register.YMM4 or Register.ZMM4 => 104,
            Register.XMM5 or Register.YMM5 or Register.ZMM5 => 105,
            Register.XMM6 or Register.YMM6 or Register.ZMM6 => 106,
            Register.XMM7 or Register.YMM7 or Register.ZMM7 => 107,
            
            Register.RIP or Register.EIP => 31, // RIP should be handled by MapMemory's RIP check, not here
            _ => 0
        };
    }
}
