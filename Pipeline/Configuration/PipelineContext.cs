using System;
using System.Collections.Generic;
using System.Diagnostics;
using Rosetta.Analysis.Resolve;
using Rosetta.Lifter.ClangRules;
using Rosetta.Binary;
using Rosetta.Config;
using Rosetta.Core;
using Rosetta.Lifter.Disassembly;
using Rosetta.Metadata;
using Rosetta.Model;

namespace Rosetta.Pipeline;

public class PipelineContext
{
    // Configuration
    public string MetadataPath { get; set; } = string.Empty;
    public string BinaryPath { get; set; } = string.Empty;
    public string? TargetAssembly { get; set; }
    public bool DumpIr { get; set; }
    public bool DumpCfg { get; set; }
    public bool DumpSsa { get; set; }
    public bool DumpAst { get; set; }
    public bool Prefer32Bit { get; set; } = false;
    public string[] args = Array.Empty<string>();

    /// <summary>
    /// Pipeline timer, started after initialization (e.g. after APK import).
    /// </summary>
    public Stopwatch Timer { get; } = new();

    /// <summary>
    /// Central configuration for the IL2CPP pipeline.
    /// </summary>
    public Il2cppConfig Config { get; set; } = new();

    // State Objects — populated by infrastructure stages
    public byte[] MetadataBytes { get; set; } = Array.Empty<byte>();
    public MetadataParser? Metadata { get; set; }
    
    public byte[] BinaryBytes { get; set; } = Array.Empty<byte>();
    public IBinaryParser? Binary { get; set; }
    
    public RegistrationResolver? Registration { get; set; }
    public MetadataBinaryBridge? Bridge { get; set; }
    public TypeResolver? TypeResolver { get; set; }
    
    /// <summary>
    /// Unified type model — the single source of truth for field layouts,
    /// method signatures, and type names. Built by TypeModelStage.
    /// </summary>
    public TypeModel? TypeModel { get; set; }
    
    public CallResolver? CallResolver { get; set; }
    public GlobalAddressMap? AddressMap { get; set; }
    public FieldRvaResolver? FieldRvaResolver { get; set; }
    public MethodDisassembler? Disassembler { get; set; }

    // ── Per-method analysis results ─────────────────────────────────────────
    // Key = global method index. Populated by AnalysisStage, consumed by AST + CodeGen.
    public Dictionary<int, MethodAnalysisResult> MethodResults { get; } = new();

    // ── Vertical pipeline output ────────────────────────────────────────────
    /// <summary>
    /// Completed assembly assets — each assembly fully processed (metadata + analysis + AST).
    /// Populated by AssemblyPipeline, consumed by CodeGenStage.
    /// </summary>
    public List<AssemblyAsset> AssemblyAssets { get; } = new();

    // ── IL2CPP Context ──────────────────────────────────────────────────────
    /// <summary>
    /// Full IL2CPP context — internal helpers operate on this.
    /// Set by Il2cppStage after pipeline completion, consumed by CodeGenStage/DumpStage.
    /// </summary>
    public Il2CppContext? Il2Cpp { get; set; }
}
