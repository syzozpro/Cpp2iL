using System.Collections.Concurrent;
using System.Collections.Generic;
using Rosetta.Analysis.Resolve;
using Rosetta.Binary;
using Rosetta.Config;
using Rosetta.Core;
using Rosetta.Lifter.ClangRules;
using Rosetta.Lifter.Disassembly;
using Rosetta.Metadata;
using Rosetta.Pipeline;

namespace Rosetta.Model;

/// <summary>
/// Self-contained data holder for the entire IL2CPP pipeline.
/// Replaces PipelineContext usage in internal helpers so they are fully
/// decoupled from the generic pipeline infrastructure.
///
/// Lifecycle:
///   1. Created by Il2cppStage (or directly via Il2cppStage.Process())
///   2. Populated step-by-step: MetadataLoader → BinaryLoader → RegistrationBridge
///      → TypeModelBuilder → DisassemblerInit → AssemblyPipeline
///   3. Results (AssemblyAssets) consumed by CodeGenStage
/// </summary>
public sealed class Il2CppContext
{
    // ─── Paths ───────────────────────────────────────────────────────────────
    public string MetadataPath { get; set; } = "";
    public string BinaryPath { get; set; } = "";

    // ─── Config ──────────────────────────────────────────────────────────────
    public Il2cppConfig Config { get; set; } = new();

    // ─── Metadata (populated by MetadataLoader) ─────────────────────────────
    public MetadataParser? Metadata { get; set; }
    public byte[] MetadataBytes { get; set; } = System.Array.Empty<byte>();

    // ─── Binary (populated by BinaryLoader) ─────────────────────────────────
    public IBinaryParser? Binary { get; set; }
    public byte[] BinaryBytes { get; set; } = System.Array.Empty<byte>();

    // ─── Registration (populated by RegistrationBridge) ─────────────────────
    public RegistrationResolver? Registration { get; set; }
    public MetadataBinaryBridge? Bridge { get; set; }
    public TypeResolver? TypeResolver { get; set; }
    public FieldRvaResolver? FieldRvaResolver { get; set; }

    // ─── Type Model (populated by TypeModelBuilder) ─────────────────────────
    public TypeModel? TypeModel { get; set; }

    // ─── Disassembly (populated by DisassemblerInit) ────────────────────────
    public CallResolver? CallResolver { get; set; }
    public GlobalAddressMap? AddressMap { get; set; }
    public MethodDisassembler? Disassembler { get; set; }

    // ─── Results (populated by AssemblyPipeline) ────────────────────────────
    public ConcurrentDictionary<int, MethodAnalysisResult> MethodResults { get; } = new();
    public List<AssemblyAsset> AssemblyAssets { get; } = new();

    // ─── Attribute Resolution (populated by AssemblyPipeline) ────────────────
    public CustomAttributeResolver? AttributeResolver { get; set; }

    /// <summary>
    /// Explicitly cleans up massive memory allocations to prevent leaks when closing/reloading.
    /// </summary>
    public void Clear()
    {
        MetadataBytes = System.Array.Empty<byte>();
        BinaryBytes = System.Array.Empty<byte>();

        Metadata?.ClearAll();
        Metadata = null;
        Binary = null;
        Registration = null;
        Bridge = null;
        TypeResolver = null;
        FieldRvaResolver = null;
        TypeModel = null;
        CallResolver = null;
        AddressMap = null;
        Disassembler = null;
        AttributeResolver = null;

        MethodResults.Clear();
        AssemblyAssets.Clear();
    }
}
