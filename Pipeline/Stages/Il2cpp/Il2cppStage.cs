// Il2cppStage — Single entry point for the entire IL2CPP pipeline.
//
// From Program.cs:  pipeline.AddStage(new Il2cppStage())  → context.AssemblyAssets populated
// Programmatic API: Il2cppStage.Process(datPath, soPath, config) → List<AssemblyAsset>
//
// Internally creates an Il2CppContext and orchestrates:
//   CLI parse → MetadataLoader → BinaryLoader → RegistrationBridge
//   → TypeModelBuilder → DisassemblerInit → AssemblyPipeline

using System;
using System.Collections.Generic;
using System.IO;
using Rosetta.Config;
using Rosetta.Model;
using Rosetta.Pipeline;

namespace Rosetta.Pipeline.Stages;

public class Il2cppStage : IPipelineStage
{
    public string Name => "IL2CPP: Full pipeline";

    // ════════════════════════════════════════════════════════════════════
    // IPipelineStage — called from PipelineExecutor
    // ════════════════════════════════════════════════════════════════════

    public void Execute(PipelineContext context)
    {
        // Step 1: Parse CLI args → populate context paths + config
        string? singleInputPath = ParseArgs(context);
        string? tempExtractDir = null;

        if (singleInputPath != null)
        {
            tempExtractDir = Path.Combine(Path.GetTempPath(), "Rosetta_Il2Cpp_Temp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempExtractDir);
            try
            {
                var importResult = Rosetta.Extractor.Imports.ImportGate.Import(singleInputPath, tempExtractDir, context.Prefer32Bit);
                context.MetadataPath = importResult.MetadataPath ?? throw new Exception("global-metadata.dat not found in input.");
                context.BinaryPath = importResult.Il2CppBinaryPath ?? throw new Exception("libil2cpp.so not found in input.");
            }
            catch
            {
                if (Directory.Exists(tempExtractDir))
                    Directory.Delete(tempExtractDir, true);
                throw;
            }
        }

        // Start the timer after import/extraction is complete
        context.Timer.Start();

        // Step 2: Build Il2CppContext from PipelineContext
        var il2cpp = new Il2CppContext
        {
            MetadataPath = context.MetadataPath,
            BinaryPath = context.BinaryPath,
            Config = context.Config,
        };

        try
        {
            // Step 3: Run the full pipeline on Il2CppContext
            RunPipeline(il2cpp);
        }
        finally
        {
            if (tempExtractDir != null && Directory.Exists(tempExtractDir))
            {
                try { Directory.Delete(tempExtractDir, true); } catch { }
            }
        }

        // Step 4: Copy results back to PipelineContext for downstream stages
        CopyResults(il2cpp, context);
    }

    // ════════════════════════════════════════════════════════════════════
    // Static API — call directly with paths, get back assemblies
    // ════════════════════════════════════════════════════════════════════

    public static List<AssemblyAsset> Process(string metadataPath, string binaryPath, Il2cppConfig? config = null)
    {
        return ProcessFull(metadataPath, binaryPath, config).AssemblyAssets.ToList();
    }

    /// <summary>Run the full pipeline and return the Il2CppContext for downstream use (e.g. CodeGenStage).</summary>
    public static Il2CppContext ProcessFull(string metadataPath, string binaryPath, Il2cppConfig? config = null)
    {
        var il2cpp = new Il2CppContext
        {
            MetadataPath = metadataPath,
            BinaryPath = binaryPath,
            Config = config ?? new Il2cppConfig(),
        };

        RunPipeline(il2cpp);

        return il2cpp;
    }

    // ════════════════════════════════════════════════════════════════════
    // Core Pipeline — operates on Il2CppContext only
    // ════════════════════════════════════════════════════════════════════

    private static void RunPipeline(Il2CppContext il2cpp)
    {
        new MetadataLoader().Run(il2cpp);
        new BinaryLoader().Run(il2cpp);
        new RegistrationBridge().Run(il2cpp);
        new TypeModelBuilder().Run(il2cpp);

        if (!Rosetta.Config.Il2cppConfig.SkipMethodBodyAnalysis)
        {
            new DisassemblerInit().Run(il2cpp);
        }

        new AssemblyPipeline().Run(il2cpp);
    }

    // ════════════════════════════════════════════════════════════════════
    // Bridge: Il2CppContext → PipelineContext (for DumpStage/CodeGenStage)
    // ════════════════════════════════════════════════════════════════════

    private static void CopyResults(Il2CppContext il2cpp, PipelineContext context)
    {
        // Store the full context for downstream stages that need it
        context.Il2Cpp = il2cpp;

        context.Metadata = il2cpp.Metadata;
        context.MetadataBytes = il2cpp.MetadataBytes;
        context.BinaryBytes = il2cpp.BinaryBytes;
        context.Binary = il2cpp.Binary;
        context.Registration = il2cpp.Registration;
        context.Bridge = il2cpp.Bridge;
        context.TypeResolver = il2cpp.TypeResolver;
        context.FieldRvaResolver = il2cpp.FieldRvaResolver;
        context.TypeModel = il2cpp.TypeModel;
        context.CallResolver = il2cpp.CallResolver;
        context.AddressMap = il2cpp.AddressMap;
        context.Disassembler = il2cpp.Disassembler;

        foreach (var asm in il2cpp.AssemblyAssets)
            context.AssemblyAssets.Add(asm);

        foreach (var (k, v) in il2cpp.MethodResults)
            context.MethodResults[k] = v;
    }

    // ════════════════════════════════════════════════════════════════════
    // CLI Argument Parsing
    // ════════════════════════════════════════════════════════════════════

    private static string? ParseArgs(PipelineContext context)
    {
        var args = context.args;
        if (args == null || args.Length == 0) return null;

        var positionalArgs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") continue;
            if (args[i] == "--select" && i + 1 < args.Length)
            {
                context.TargetAssembly = args[++i];
                context.Config.TargetAssembly = context.TargetAssembly;
            }
            else if (args[i] == "--arch-32")
            {
                context.Prefer32Bit = true;
            }
            else if (args[i] == "--arch-64")
            {
                context.Prefer32Bit = false;
            }
            else if (args[i] == "--dump-ir")
            {
                context.DumpIr = true;
                Rosetta.Config.Il2cppConfig.DumpIr = true;
            }
            else if (args[i] == "--skip-analysis")
            {
                Rosetta.Config.Il2cppConfig.SkipMethodBodyAnalysis = true;
            }
            else if (args[i] == "--output" && i + 1 < args.Length)
            {
                context.Config.OutputDirectory = args[++i];
            }
            else if (args[i] == "--verbose" || args[i] == "-v")
            {
                ConsoleReporter.Verbose = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    ConsoleReporter.SetVerboseFilters(args[++i]);
                }
            }
            else
            {
                positionalArgs.Add(args[i]);
            }
        }

        if (positionalArgs.Count == 1)
        {
            if (Rosetta.Config.Il2cppConfig.SkipMethodBodyAnalysis)
                ConsoleReporter.Info("  Mode: Metadata-only (--skip-analysis)");
            return positionalArgs[0]; // Return the single input path for extraction
        }

        context.MetadataPath = positionalArgs.Count > 0
            ? positionalArgs[0]
            : Path.Combine(FindGameDataRoot(), "assets", "bin", "Data", "Managed", "Metadata", "global-metadata.dat");

        context.BinaryPath = positionalArgs.Count > 1
            ? positionalArgs[1]
            : Path.Combine(FindGameDataRoot(), "lib", "arm64-v8a", "libil2cpp.so");

        if (Rosetta.Config.Il2cppConfig.SkipMethodBodyAnalysis)
            ConsoleReporter.Info("  Mode: Metadata-only (--skip-analysis)");
            
        return null;
    }

    private static string FindGameDataRoot()
    {
        string currentDir = Environment.CurrentDirectory;
        string? targetDir = currentDir;

        while (targetDir != null)
        {
            if (Directory.Exists(Path.Combine(targetDir, "assets", "bin", "Data")))
                return targetDir;
            targetDir = Path.GetDirectoryName(targetDir);
        }

        return currentDir;
    }
}
