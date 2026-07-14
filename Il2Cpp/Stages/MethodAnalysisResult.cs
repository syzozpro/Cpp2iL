using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Analysis.AST;
using Rosetta.Analysis.RegisterState;
using Rosetta.Binary;
using Rosetta.Lifter.IR;

namespace Rosetta.Pipeline;

/// <summary>
/// Per-method analysis result — holds every stage's output for a single method.
/// Created by AnalysisStage, consumed by AST structuring and CodeGen.
///
/// Data flows:  ARM64 → IrMethod → CFG → SSA → RegisterStateMap → AST → C#
/// </summary>
public sealed class MethodAnalysisResult
{
    /// <summary>Global method index in metadata.</summary>
    public int MethodIndex { get; init; }

    /// <summary>Flat IR representation (output of lifter + data resolver).</summary>
    public IrMethod IrMethod { get; set; } = null!;

    /// <summary>Control flow graph built from the IR.</summary>
    public IrControlFlowGraph? Cfg { get; set; }

    /// <summary>SSA context with Def-Use chains (built on top of CFG).</summary>
    public SsaContext? Ssa { get; set; }

    /// <summary>Def-Use analyzer for data flow queries.</summary>
    public DefUseAnalyzer? Dfa { get; set; }

    /// <summary>Forward register state map — tracks what each register holds at each instruction.</summary>
    public RegisterStateMap? RegState { get; set; }

    /// <summary>Structured AST (output of AstBuilder).</summary>
    public AstMethod? Ast { get; set; }

    /// <summary>Generated C# source code (output of CSharpEmitter).</summary>
    public string? CSharpCode { get; set; }

    // ── Capstone comparison data (only populated when --dump-ir is active) ──

    /// <summary>Raw ARM64 instructions from the custom decoder (for Capstone comparison).</summary>
    public Arm64Instruction[]? RawArm64Instructions { get; set; }

    /// <summary>Raw Thumb2 instructions from the custom decoder (for Capstone comparison).</summary>
    public Thumb2Instruction[]? RawThumb2Instructions { get; set; }

    /// <summary>Raw binary bytes of the method body (for Capstone re-disassembly).</summary>
    public byte[]? RawMethodBytes { get; set; }

    /// <summary>Base virtual address of the method (for Capstone).</summary>
    public ulong MethodVA { get; set; }

    /// <summary>True if this ARM32 method uses ARM mode (bit 0 = 0); false for Thumb mode.</summary>
    public bool IsArmMode { get; set; }
}

