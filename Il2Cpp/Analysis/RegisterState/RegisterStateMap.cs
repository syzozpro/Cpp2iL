// RegisterStateMap — Forward data-flow pass that tracks what each register holds
// at every instruction point in a method.
//
// This replaces ALL per-site heuristics (backward scans, pattern matching) with a
// single, deterministic forward pass. The AST builder queries this map instead of
// re-discovering context at each use site.
//
// Architecture:
//   AnalysisStage builds the map once per method.
//   AstBuilder reads it at each instruction to know:
//     - What type does register X hold? (for field access, receiver resolution)
//     - Is register X the 'this' pointer? (for method calls)
//     - What value was stored at SP+offset? (for boxing, spill resolution)
//     - What was the last write prefix (w/x) for this register? (for naming)

using System;
using System.Collections.Generic;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Analysis.AST;

namespace Rosetta.Analysis.RegisterState;


/// <summary>
/// Per-method register state map. Built once by a forward pass,
/// queried by the AST builder at each instruction.
/// </summary>
public sealed class RegisterStateMap
{
    /// <summary>
    /// State BEFORE each instruction executes.
    /// Key = instruction index in the flat IR list.
    /// </summary>
    private readonly Dictionary<int, InstructionState> _stateAtInst = new();

    /// <summary>
    /// Canonical name (first-definition name) for each GP register.
    /// Key = register number (0-30), Value = "w8" or "x8".
    /// </summary>
    private readonly Dictionary<int, string> _canonicalNames = new();

    /// <summary>
    /// Maps instruction object references to their flat-list index.
    /// This allows querying by instruction reference (as used in CFG blocks).
    /// </summary>
    private readonly Dictionary<IrInstruction, int> _instToIndex = new();

    private bool _isArm32;

    // ═══════════════════════════════════════════════════════════════════
    // Query API — by flat index
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Get the register state at a specific instruction index.</summary>
    public InstructionState? GetStateAt(int instructionIndex)
    {
        return _stateAtInst.TryGetValue(instructionIndex, out var state) ? state : null;
    }

    /// <summary>Get info about a specific GP register at a specific instruction.</summary>
    public RegInfo? GetRegAt(int instructionIndex, int regNum)
    {
        if (regNum < 0 || regNum > 30) return null;
        return _stateAtInst.TryGetValue(instructionIndex, out var state)
            ? state.GpRegs[regNum]
            : null;
    }

    /// <summary>Get the canonical variable name for a register (based on first definition).</summary>
    public string GetCanonicalName(int regNum)
    {
        return _canonicalNames.TryGetValue(regNum, out var name) ? name : (regNum <= 15 ? $"R{regNum}" : $"x{regNum}");
    }

    /// <summary>Check if a register holds 'this' at a given instruction.</summary>
    public bool IsThis(int instructionIndex, int regNum)
    {
        var info = GetRegAt(instructionIndex, regNum);
        if (info == null) return false;
        if (info.Kind == RegValueKind.This) return true;
        // Follow copy chains: if reg was copied from another reg that is 'this'
        if (info.Kind == RegValueKind.Copied && info.SourceReg >= 0)
            return GetRegAt(instructionIndex, info.SourceReg)?.Kind == RegValueKind.This;
        return false;
    }

    /// <summary>Get the type held by a register at a given instruction, following copies.</summary>
    public string? GetTypeAt(int instructionIndex, int regNum)
    {
        var info = GetRegAt(instructionIndex, regNum);
        if (info == null) return null;
        if (info.TypeName != null) return info.TypeName;
        // Follow copy chains
        if (info.Kind == RegValueKind.Copied && info.SourceReg >= 0)
            return GetRegAt(instructionIndex, info.SourceReg)?.TypeName;
        return null;
    }

    /// <summary>Get what was stored at an SP-relative slot at a given instruction.</summary>
    public RegInfo? GetSpSlot(int instructionIndex, long offset)
    {
        if (!_stateAtInst.TryGetValue(instructionIndex, out var state)) return null;
        return state.SpSlots.TryGetValue(offset, out var info) ? info : null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Query API — by instruction reference (for use from CFG blocks)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Get the flat-list index for an instruction object.</summary>
    public int GetIndex(IrInstruction inst)
    {
        return _instToIndex.TryGetValue(inst, out var idx) ? idx : -1;
    }

    /// <summary>Check if a register holds 'this' at a given instruction.</summary>
    public bool IsThis(IrInstruction inst, int regNum) =>
        IsThis(GetIndex(inst), regNum);

    /// <summary>Get the type held by a register at a given instruction.</summary>
    public string? GetTypeAt(IrInstruction inst, int regNum) =>
        GetTypeAt(GetIndex(inst), regNum);

    /// <summary>Get info about a GP register at a given instruction.</summary>
    public RegInfo? GetRegAt(IrInstruction inst, int regNum) =>
        GetRegAt(GetIndex(inst), regNum);

    // ═══════════════════════════════════════════════════════════════════
    // Builder — builds the map via a forward pass
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build the register state map for a method by walking instructions forward.
    /// Uses a two-phase approach:
    ///   Phase 1: Pre-scan to discover which registers are used as arrays (usage-based inference)
    ///   Phase 2: Forward pass to track all register states, with array info from Phase 1
    /// </summary>
    public static RegisterStateMap Build(IrMethod method)
    {
        var map = new RegisterStateMap();
        var state = new InstructionState();
        var insts = method.Instructions;
        var firstDef = new HashSet<int>(); // registers that have been defined

        // ═══ Phase 1: Pre-scan for array registers ═══
        // Scan ALL instructions to find registers accessed at offset 0x18 with ".Length"
        // annotation. This tells us which registers hold arrays, BEFORE we process them.
        // Universal: works for new T[N], Split(), GetComponents(), ToArray(), etc.
        var arrayRegs = new HashSet<int>();
        for (int i = 0; i < insts.Count; i++)
        {
            var inst = insts[i];
            if (inst.Opcode == IrOpcode.Load && inst.Sources.Length > 0)
            {
                var src = inst.Sources[0];
                // load [regN + 0x18] with ".Length" → regN is an array
                if (src.Kind == IrOperandKind.Memory && src.Value >= 0 && src.Value <= 30 &&
                    src.Offset == 0x18 && inst.Annotation != null &&
                    inst.Annotation.Contains("Length"))
                {
                    arrayRegs.Add((int)src.Value);
                }
            }
        }

        // Detect architecture from the method's instructions
        bool isArm32 = false;
        if (insts.Count > 0 && insts[0].Destination is IrOperand firstDest)
        {
            isArm32 = firstDest.Kind == IrOperandKind.Register && firstDest.Name != null && firstDest.Name.StartsWith('R');
        }
        map._isArm32 = isArm32;

        // ═══ Phase 2: Forward pass ═══
        // Instance methods: R0/X0 = this at entry
        if (!method.IsStatic)
        {
            byte thisBitWidth = (byte)(isArm32 ? 32 : 64);
            string thisCanonical = Rosetta.Analysis.Utils.ArmUtils.GetRegisterPrefix(0, thisBitWidth, isArm32);
            state.GpRegs[0] = new RegInfo
            {
                Kind = RegValueKind.This,
                TypeName = method.DeclaringType,
                DefIndex = -1,
                DefBitWidth = thisBitWidth,
                CanonicalName = thisCanonical
            };
            map._canonicalNames[0] = thisCanonical;
            firstDef.Add(0);
        }

        for (int i = 0; i < insts.Count; i++)
        {
            // Register instruction → index mapping
            map._instToIndex[insts[i]] = i;

            // Snapshot state BEFORE this instruction
            map._stateAtInst[i] = state.Clone();

            var inst = insts[i];

            // Process the instruction to update state
            ProcessInstruction(inst, state, map, firstDef, i, arrayRegs);
        }

        return map;
    }

    private static void ProcessInstruction(
        IrInstruction inst, InstructionState state, RegisterStateMap map,
        HashSet<int> firstDef, int instIdx, HashSet<int> arrayRegs)
    {
        // ─── Store to SP-relative slot ───
        if (inst.Opcode == IrOpcode.Store && inst.Sources.Length >= 2)
        {
            var dst = inst.Sources[0];
            var val = inst.Sources[1];

            if (dst.Kind == IrOperandKind.Memory && Rosetta.Analysis.Utils.ArmUtils.IsStackPointer(dst.Value))
            {
                RegInfo? valInfo = null;
                if (val.Kind == IrOperandKind.Register && val.Value >= 0 && val.Value <= 30)
                    valInfo = state.GpRegs[val.Value];
                else if (val.Kind == IrOperandKind.Immediate)
                    valInfo = new RegInfo { Kind = RegValueKind.Literal, IntValue = val.Value, DefIndex = instIdx };

                if (valInfo != null)
                    state.SpSlots[dst.Offset] = valInfo;
            }
        }

        // ─── Register definitions ───
        if (!inst.Destination.HasValue) return;
        var destOp = inst.Destination.Value;
        if (destOp.Kind != IrOperandKind.Register) return;
        int regNum = (int)destOp.Value;
        if (regNum < 0 || regNum > 30) return;

        // Use named register if available, fall back to ARM64 convention
        string prefix;
        string? destName = destOp.Name;
        if (destName != null && !destName.StartsWith('S') && !destName.StartsWith('D'))
        {
            // Named register (ARM32 R0-R15 or special names like SP/LR/PC)
            prefix = destName;
        }
        else
        {
            prefix = Rosetta.Analysis.Utils.ArmUtils.GetRegisterPrefix(regNum, destOp.BitWidth, map._isArm32);
        }
        string regName = prefix;

        // Track canonical name (first definition wins)
        if (firstDef.Add(regNum))
        {
            map._canonicalNames[regNum] = regName;
        }

        // Determine what this instruction puts into the register
        RegInfo info = AnalyzeDefinition(inst, state, regNum, regName, instIdx, destOp.BitWidth);

        // ─── Array upgrade (from pre-scan) ───
        // If the pre-scan determined this register is used as an array later,
        // upgrade its kind from CallResult/Copied/Unknown → ArrayRef.
        // This handles Split(), GetComponents(), ToArray(), etc. universally.
        if (arrayRegs.Contains(regNum) && info.Kind != RegValueKind.ArrayRef)
        {
            info = new RegInfo
            {
                Kind = RegValueKind.ArrayRef,
                TypeName = info.TypeName,
                Value = info.Value,
                SourceReg = info.SourceReg,
                BaseReg = info.BaseReg,
                Offset = info.Offset,
                DefIndex = info.DefIndex,
                DefBitWidth = info.DefBitWidth,
                CanonicalName = info.CanonicalName
            };
        }

        state.GpRegs[regNum] = info;
    }

    private static RegInfo AnalyzeDefinition(
        IrInstruction inst, InstructionState state,
        int regNum, string regName, int instIdx, byte bitWidth)
    {
        return inst.Opcode switch
        {
            IrOpcode.LoadAddress => AnalyzeLoadAddress(inst, instIdx, bitWidth, regName),
            IrOpcode.Load => AnalyzeLoad(inst, state, instIdx, bitWidth, regName),
            IrOpcode.LoadImmediate => AnalyzeLoadImmediate(inst, instIdx, bitWidth, regName),
            IrOpcode.Assign => AnalyzeAssign(inst, state, instIdx, bitWidth, regName),
            IrOpcode.Call or IrOpcode.IndirectCall => AnalyzeCall(inst, instIdx, bitWidth, regName),
            IrOpcode.Add or IrOpcode.Sub or IrOpcode.Mul or IrOpcode.And or IrOpcode.Or => AnalyzeAlu(inst, instIdx, bitWidth, regName),
            _ => new RegInfo { Kind = RegValueKind.Unknown, DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName }
        };
    }

    private static RegInfo AnalyzeLoadAddress(IrInstruction inst, int instIdx, byte bitWidth, string regName)
    {
        if (inst.Annotation == "il2cpp_metadata_page")
            return new RegInfo
            {
                Kind = RegValueKind.MetadataPage,
                DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
            };

        return new RegInfo
        {
            Kind = RegValueKind.AddressOf,
            Value = inst.Annotation,
            DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
        };
    }

    private static RegInfo AnalyzeLoad(IrInstruction inst, InstructionState state, int instIdx, byte bitWidth, string regName)
    {
        string? ann = inst.Annotation;

        if (ann != null)
        {
            if (ann.StartsWith("typeof(") && ann.EndsWith(")"))
            {
                string typeName = ann[7..^1];
                return new RegInfo
                {
                    Kind = RegValueKind.TypeOf,
                    TypeName = typeName,
                    Value = ann,
                    DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
                };
            }

            if (ann.StartsWith("\""))
            {
                string strVal = ann.Trim('"');
                return new RegInfo
                {
                    Kind = RegValueKind.StringLiteral,
                    TypeName = "System.String",
                    Value = strVal,
                    DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
                };
            }

            if (ann == "metadata_var")
                return new RegInfo
                {
                    Kind = RegValueKind.FieldValue,
                    Value = "metadata_var",
                    DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
                };

            if (ann.StartsWith("this.") || ann.StartsWith("."))
                return new RegInfo
                {
                    Kind = RegValueKind.FieldValue,
                    Value = ann,
                    DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
                };
        }

        if (inst.Sources.Length > 0 && inst.Sources[0].Kind == IrOperandKind.Memory)
        {
            int baseReg = (int)inst.Sources[0].Value;
            long offset = inst.Sources[0].Offset;

            if (baseReg >= 0 && baseReg <= 30 && state.GpRegs[baseReg] != null)
            {
                var baseInfo = state.GpRegs[baseReg];
                if (baseInfo!.Kind == RegValueKind.TypeOf)
                    return new RegInfo
                    {
                        Kind = RegValueKind.TypeOf,
                        TypeName = baseInfo.TypeName,
                        Value = baseInfo.Value,
                        BaseReg = baseReg, Offset = offset,
                        DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
                    };
            }

            return new RegInfo
            {
                Kind = RegValueKind.FieldValue,
                BaseReg = baseReg, Offset = offset,
                DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
            };
        }

        return new RegInfo
        {
            Kind = RegValueKind.Unknown,
            DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
        };
    }

    private static RegInfo AnalyzeLoadImmediate(IrInstruction inst, int instIdx, byte bitWidth, string regName)
    {
        if (inst.Sources.Length > 0)
        {
            var src = inst.Sources[0];
            if (src.Kind == IrOperandKind.Immediate)
                return new RegInfo
                {
                    Kind = RegValueKind.Literal,
                    IntValue = src.Value,
                    TypeName = bitWidth <= 32 ? "int" : "long",
                    DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
                };
            if (src.Kind == IrOperandKind.FloatImmediate)
                return new RegInfo
                {
                    Kind = RegValueKind.Literal,
                    IntValue = src.Value,
                    TypeName = bitWidth <= 32 ? "float" : "double",
                    DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
                };
        }

        return new RegInfo
        {
            Kind = RegValueKind.Unknown,
            DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
        };
    }

    private static RegInfo AnalyzeAssign(IrInstruction inst, InstructionState state, int instIdx, byte bitWidth, string regName)
    {
        if (inst.Sources.Length > 0 && inst.Sources[0].Kind == IrOperandKind.Register)
        {
            int srcReg = (int)inst.Sources[0].Value;
            if (srcReg >= 0 && srcReg <= 30 && state.GpRegs[srcReg] != null)
            {
                var srcInfo = state.GpRegs[srcReg]!;
                return new RegInfo
                {
                    Kind = RegValueKind.Copied,
                    TypeName = srcInfo.TypeName,
                    Value = srcInfo.Value,
                    SourceReg = srcReg,
                    DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
                };
            }
        }

        return new RegInfo
        {
            Kind = RegValueKind.Unknown,
            DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
        };
    }

    private static RegInfo AnalyzeCall(IrInstruction inst, int instIdx, byte bitWidth, string regName)
    {
        string? callType = null;
        string? callAnnotation = inst.Annotation;

        if (callAnnotation != null)
        {
            var parsed = Rosetta.Analysis.Utils.AnnotationParser.Parse(callAnnotation);
            callType = parsed.TypeName;

            if (callAnnotation.StartsWith("new ") && callAnnotation.Contains('['))
            {
                int bracketIdx = callAnnotation.IndexOf('[');
                string arrayType = callAnnotation[4..bracketIdx];
                return new RegInfo
                {
                    Kind = RegValueKind.ArrayRef,
                    TypeName = arrayType + "[]",
                    Value = callAnnotation,
                    DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
                };
            }

            if (callAnnotation.StartsWith("new ") && callAnnotation.EndsWith("()"))
            {
                string objType = callAnnotation[4..^2];
                return new RegInfo
                {
                    Kind = RegValueKind.ObjectRef,
                    TypeName = objType,
                    Value = callAnnotation,
                    DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
                };
            }

            if (callAnnotation == "string_intern")
                return new RegInfo
                {
                    Kind = RegValueKind.StringLiteral,
                    TypeName = "System.String",
                    Value = "string_intern",
                    DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
                };
        }

        return new RegInfo
        {
            Kind = RegValueKind.CallResult,
            TypeName = callType,
            Value = callAnnotation,
            DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
        };
    }

    private static RegInfo AnalyzeAlu(IrInstruction inst, int instIdx, byte bitWidth, string regName)
    {
        return new RegInfo
        {
            Kind = RegValueKind.Literal,
            TypeName = bitWidth <= 32 ? "int" : "long",
            DefIndex = instIdx, DefBitWidth = bitWidth, CanonicalName = regName
        };
    }
}
