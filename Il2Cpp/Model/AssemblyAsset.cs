using System;
using System.Collections.Generic;
using Rosetta.Metadata;

namespace Rosetta.Model;

/// <summary>
/// Container for all types within a single IL2CPP assembly.
/// Created at the start of processing each assembly, populated with ScriptAssets
/// as each type is processed vertically through all stages.
///
/// Data lifecycle:
///   1. Created by AssemblyPipelineStage with assembly identity
///   2. ScriptAssets added as each type finishes full vertical processing
///   3. Consumed by CodeGenStage to emit per-assembly output directories
/// </summary>
public sealed class AssemblyAsset
{
    // ─── Identity ────────────────────────────────────────────────────────────

    /// <summary>Index into Assemblies array in metadata.</summary>
    public int AssemblyIndex { get; init; }

    /// <summary>Assembly name (e.g. "Assembly-CSharp").</summary>
    public string Name { get; init; } = "";

    /// <summary>Reference to the raw AssemblyDef from metadata.</summary>
    public AssemblyDef AssemblyDef { get; init; } = null!;

    /// <summary>Reference to the ImageDefinition for this assembly.</summary>
    public ImageDefinition ImageDef { get; init; } = null!;

    // ─── Version Info ────────────────────────────────────────────────────────

    public int MajorVersion { get; init; }
    public int MinorVersion { get; init; }
    public int BuildVersion { get; init; }
    public int RevisionVersion { get; init; }

    /// <summary>Version string (e.g. "1.0.0.0").</summary>
    public string VersionString => $"{MajorVersion}.{MinorVersion}.{BuildVersion}.{RevisionVersion}";

    // ─── Type Range ──────────────────────────────────────────────────────────

    /// <summary>Start index in the global TypeDefinitions array.</summary>
    public int TypeStart { get; init; }

    /// <summary>Number of types in this assembly.</summary>
    public int TypeCount { get; init; }

    // ─── Script Assets ───────────────────────────────────────────────────────

    /// <summary>
    /// All top-level ScriptAssets in this assembly, in processing order.
    /// Nested types are stored within their parent ScriptAsset.NestedTypes.
    /// </summary>
    public List<ScriptAsset> Scripts { get; } = new();

    /// <summary>
    /// Dictionary of all structs in this assembly, mapping their full name to the ScriptAsset.
    /// </summary>
    public Dictionary<string, ScriptAsset> Structs { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ─── Namespace Info ──────────────────────────────────────────────────────

    /// <summary>All unique namespaces found in this assembly.</summary>
    public HashSet<string> Namespaces { get; } = new();

    // ─── Stats ───────────────────────────────────────────────────────────────

    public int TotalTypes { get; set; }
    public int TotalMethods { get; set; }
    public int AnalyzedMethods { get; set; }
    public int FailedMethods { get; set; }
    public int EmittedFiles { get; set; }

    /// <summary>Whether this assembly should be exported to disk. False for Engine/BCL assemblies.</summary>
    public bool ShouldExport { get; set; } = true;

    // ─── Factory ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Create an AssemblyAsset from metadata assembly and image definitions.
    /// </summary>
    public static AssemblyAsset Create(int asmIdx, AssemblyDef asm, ImageDefinition img)
    {
        int typeEnd = img.TypeStart + (int)img.TypeCount;

        return new AssemblyAsset
        {
            AssemblyIndex = asmIdx,
            Name = asm.Name ?? $"Assembly_{asmIdx}",
            AssemblyDef = asm,
            ImageDef = img,
            MajorVersion = asm.Major,
            MinorVersion = asm.Minor,
            BuildVersion = asm.Build,
            RevisionVersion = asm.Revision,
            TypeStart = img.TypeStart,
            TypeCount = (int)img.TypeCount,
        };
    }
}
