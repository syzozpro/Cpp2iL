using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Rosetta.Analysis.AST;
using Rosetta.Analysis.AST.Passes;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Analysis.RegisterState;
using Rosetta.Binary;
using Rosetta.Lifter.IR;
using Rosetta.Metadata;
using Rosetta.Model;
using Rosetta.Pipeline;
using Rosetta.Config;

namespace Rosetta.Pipeline.Stages;

/// <summary>
/// The vertical pipeline orchestrator with two-level parallelism.
///
/// Level 1: Assemblies are processed in parallel (Parallel.ForEach).
/// Level 2: Within each assembly, scripts are analyzed in parallel using
///          thread-local analysis tools (each thread gets its own IrLifter,
///          IrDataResolver, IrCfgBuilder, SsaBuilder, SsaAstBuilder).
///
/// Infrastructure stages (metadata, binary, registration, TypeModel, disassembler)
/// still run horizontally BEFORE this stage. This stage handles:
///   1. Grouping types by assembly
///   2. Creating AssemblyAsset and ScriptAsset instances
///   3. Running per-type analysis (ARM64 → IR → CFG → SSA) if enabled — IN PARALLEL
///   4. Running per-type AST structuring (SSA → AST) if enabled — IN PARALLEL
///   5. Collecting results into context.AssemblyAssets
///   6. Sequential post-parallel ScriptLinker pass for base-type resolution
///
/// CodeGenStage runs AFTER this stage, consuming context.AssemblyAssets.
/// </summary>
public sealed class AssemblyPipeline
{
    public void Run(Il2CppContext context)
    {
        if (context.Metadata == null)
            throw new InvalidOperationException("Metadata not loaded");

        var config = context.Config;
        ConsoleReporter.SetCategory("IL2Cpp");

        bool doAnalysis = !Il2cppConfig.SkipMethodBodyAnalysis
            && context.Disassembler != null
            && context.Bridge != null
            && context.CallResolver != null
            && context.TypeResolver != null;

        bool isArm32 = context.Binary?.Is32Bit == true;

        int totalAssemblies = 0;
        int totalTypes = 0;
        int totalMethodsAnalyzed = 0;
        int totalMethodsFailed = 0;
        int skippedRefAssemblies = 0;

        // ── Attribute resolver (created once, used for all assemblies — thread-safe: read-only) ──
        context.AttributeResolver = new CustomAttributeResolver(context.Metadata, context.TypeResolver);

        // ── Build list of assemblies to process ─────────────────────────────
        var assembliesToProcess = new List<(int asmIdx, AssemblyDef asm, ImageDefinition img)>();
        for (int asmIdx = 0; asmIdx < context.Metadata.Assemblies.Length; asmIdx++)
        {
            var asm = context.Metadata.Assemblies[asmIdx];
            if (asm.ImageIndex < 0 || asm.ImageIndex >= context.Metadata.ImageDefinitions.Length)
                continue;

            string rawAsmName = asm.Name ?? $"Assembly_{asmIdx}";
            string cleanAsmName = rawAsmName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? rawAsmName[..^4]
                : rawAsmName;

            // Filter by target assembly based on Advanced Settings
            if (Il2cppConfig.TargetAssemblies == Il2cppConfig.TargetAssemblyMode.AssemblyCSharp)
            {
                if (!cleanAsmName.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else if (Il2cppConfig.TargetAssemblies == Il2cppConfig.TargetAssemblyMode.MainAssemblies)
            {
                if (!cleanAsmName.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) && 
                    !cleanAsmName.Equals("Assembly-CSharp-firstpass", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else if (!string.IsNullOrEmpty(config.TargetAssembly) && 
                     !cleanAsmName.Equals(config.TargetAssembly, StringComparison.OrdinalIgnoreCase) && 
                     !rawAsmName.Equals(config.TargetAssembly, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var exportType = AssemblyClassifier.Classify(rawAsmName);
            if (exportType != AssemblyExportType.Decompile)
            {
                if(exportType == AssemblyExportType.UnityEngineAssembly)
                {
                    
                }
                else
                {
                    Interlocked.Increment(ref skippedRefAssemblies);
                    continue;
                }
            }

            var img = context.Metadata.ImageDefinitions[asm.ImageIndex];
            assembliesToProcess.Add((asmIdx, asm, img));
        }

        // ── Shared dictionary for resolving BaseAsset (populated post-parallel) ──
        var scriptByName = new Dictionary<string, ScriptAsset>(4096, StringComparer.OrdinalIgnoreCase);
        var pending = new List<ScriptAsset>();

        // ── Collect completed assemblies from parallel processing ────────────
        var completedAssemblies = new ConcurrentBag<AssemblyAsset>();

        // ── Level 1: Process each assembly in parallel ──────────────────────
        Parallel.ForEach(assembliesToProcess, assemblyInfo =>
        {
            var (asmIdx, asm, img) = assemblyInfo;
            var assemblyAsset = AssemblyAsset.Create(asmIdx, asm, img);

            string rawAsmName = asm.Name ?? $"Assembly_{asmIdx}";
            bool isUnityEngineAssembly = AssemblyClassifier.Classify(rawAsmName) == AssemblyExportType.UnityEngineAssembly;
            assemblyAsset.ShouldExport = !isUnityEngineAssembly;

            int typeEnd = assemblyAsset.TypeStart + assemblyAsset.TypeCount;
            if (typeEnd > context.Metadata.TypeDefinitions.Length)
                typeEnd = context.Metadata.TypeDefinitions.Length;

            // ── Build list of top-level type indices ─────────────────────────
            var typeIndices = new List<int>();
            for (int t = assemblyAsset.TypeStart; t < typeEnd; t++)
            {
                var td = context.Metadata.TypeDefinitions[t];
                if (td.DeclaringTypeIndex >= 0) continue; // skip nested — handled inside parent
                // if (isUnityEngineAssembly && !td.IsStruct) continue;
                typeIndices.Add(t);
            }

            // ── Level 2: Process scripts in parallel within this assembly ───
            var parallelScripts = new ConcurrentBag<ScriptAsset>();
            var parallelNamespaces = new ConcurrentBag<string>();
            int asmMethodsAnalyzed = 0;
            int asmMethodsFailed = 0;

            if (doAnalysis)
            {
                // Parallel with thread-local analysis tools
                Parallel.ForEach(
                    typeIndices,
                    // Thread-local initializer: each thread gets its own set of analysis tools
                    () =>
                    {
                        IrLifter? tLifter = null;
                        Thumb2IrLifter? tThumb2Lifter = null;
                        X64IrLifter? tX64Lifter = null;
                        bool isX64 = context.Binary?.IsX64 == true;

                        if (isArm32)
                            tThumb2Lifter = new Thumb2IrLifter(context.CallResolver!, context.Binary, context.BinaryBytes);
                        else if (isX64)
                            tX64Lifter = new X64IrLifter(context.CallResolver!, context.AddressMap);
                        else
                            tLifter = new IrLifter(context.CallResolver!, context.Binary, context.BinaryBytes);

                        // Capstone wrapper: only created when --dump-ir is active
                        CapstoneDisassemblerWrapper? tCapstone = null;
                        if (Il2cppConfig.DumpIr && !isX64)
                        {
                            try { tCapstone = new CapstoneDisassemblerWrapper(isArm32); }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[PIPELINE-WARN] Capstone native library not available — comparison disabled: {ex.Message}");
                            }
                        }

                        return (
                            Lifter: tLifter,
                            Thumb2Lifter: tThumb2Lifter,
                            X64Lifter: tX64Lifter,
                            IsX64: isX64,
                            DataResolver: new IrDataResolver(context.AddressMap, context.Metadata, context.Registration, context.TypeResolver, context.TypeModel),
                            CfgBuilder: new IrCfgBuilder(),
                            SsaBuilder: new SsaBuilder(),
                            AstBuilder: new SsaAstBuilder(),
                            Capstone: tCapstone,
                            Analyzed: 0,
                            Failed: 0
                        );
                    },
                    // Body: process one type per iteration
                    (t, loopState, localTools) =>
                    {
                        var td = context.Metadata.TypeDefinitions[t];
                        var script = ScriptAssetBuilder.Build(t, td, context.Metadata, context.TypeResolver, null);

                        // Process nested types recursively
                        ScriptLinker.PopulateNestedTypes(script, td, context, false);

                        if (isUnityEngineAssembly)
                        {
                            FilterUnityEngineScript(script);
                        }

                        // Resolve custom attributes for type + all members
                        ScriptAssetBuilder.PopulateAttributes(script, context.AttributeResolver, img);

                        // Run analysis + AST
                        string? prevTarget = ConsoleReporter.ActiveTarget;
                        int analyzed = 0, failed = 0;
                        try
                        {
                            ConsoleReporter.ActiveTarget = script.FullName;
                            if (!isUnityEngineAssembly)
                            {
                                (analyzed, failed) = ProcessScriptAnalysis(
                                    script, context,
                                    localTools.Lifter, localTools.Thumb2Lifter, localTools.X64Lifter, isArm32, localTools.IsX64,
                                    localTools.DataResolver, localTools.CfgBuilder,
                                    localTools.SsaBuilder, localTools.AstBuilder,
                                    localTools.Capstone);
                            }
                        }
                        finally
                        {
                            ConsoleReporter.ActiveTarget = prevTarget;
                        }

                        parallelScripts.Add(script);
                        parallelNamespaces.Add(script.Namespace);

                        return (
                            localTools.Lifter,
                            localTools.Thumb2Lifter,
                            localTools.X64Lifter,
                            localTools.IsX64,
                            localTools.DataResolver,
                            localTools.CfgBuilder,
                            localTools.SsaBuilder,
                            localTools.AstBuilder,
                            localTools.Capstone,
                            Analyzed: localTools.Analyzed + analyzed,
                            Failed: localTools.Failed + failed
                        );
                    },
                    // Final: accumulate per-thread totals
                    localTools =>
                    {
                        Interlocked.Add(ref asmMethodsAnalyzed, localTools.Analyzed);
                        Interlocked.Add(ref asmMethodsFailed, localTools.Failed);
                        localTools.Capstone?.Dispose();
                    }
                );
            }
            else
            {
                // No analysis — just build scripts (metadata only, lightweight)
                // Still parallel for script construction
                Parallel.ForEach(typeIndices, t =>
                {
                    var td = context.Metadata.TypeDefinitions[t];
                    var script = ScriptAssetBuilder.Build(t, td, context.Metadata, context.TypeResolver, null);
                    ScriptLinker.PopulateNestedTypes(script, td, context, false);

                    if (isUnityEngineAssembly)
                    {
                        FilterUnityEngineScript(script);
                    }

                    ScriptAssetBuilder.PopulateAttributes(script, context.AttributeResolver, img);

                    parallelScripts.Add(script);
                    parallelNamespaces.Add(script.Namespace);
                });
            }

            // ── Transfer results to assembly asset ──────────────────────────
            // Sort scripts by TypeIndex to maintain deterministic output order
            var sortedScripts = new List<ScriptAsset>(parallelScripts);
            sortedScripts.Sort((a, b) => a.TypeIndex.CompareTo(b.TypeIndex));
            foreach (var script in sortedScripts)
            {
                assemblyAsset.Scripts.Add(script);
                if (isUnityEngineAssembly)
                {
                    AddStructsToAssemblyAsset(script, assemblyAsset);
                }
            }

            foreach (var ns in parallelNamespaces)
                assemblyAsset.Namespaces.Add(ns);

            assemblyAsset.TotalTypes = sortedScripts.Count;
            assemblyAsset.AnalyzedMethods = asmMethodsAnalyzed;
            assemblyAsset.FailedMethods = asmMethodsFailed;
            assemblyAsset.TotalMethods = asmMethodsAnalyzed + asmMethodsFailed;

            completedAssemblies.Add(assemblyAsset);

            Interlocked.Increment(ref totalAssemblies);
            Interlocked.Add(ref totalTypes, assemblyAsset.TotalTypes);
            Interlocked.Add(ref totalMethodsAnalyzed, asmMethodsAnalyzed);
            Interlocked.Add(ref totalMethodsFailed, asmMethodsFailed);

            // Free per-assembly method results — CodeGen reads AST from ScriptAsset, not MethodResults
            // But keep them if --dump-ir is active, so DumpStage can write them out
            if (!Il2cppConfig.DumpIr)
                context.MethodResults.Clear();
        });
        // ── Post-parallel: Sequential base-type linking ────────────────────
        // Sort assemblies by index for deterministic ordering
        var sortedAssemblies = new List<AssemblyAsset>(completedAssemblies);
        sortedAssemblies.Sort((a, b) => a.AssemblyIndex.CompareTo(b.AssemblyIndex));

        foreach (var assemblyAsset in sortedAssemblies)
        {
            foreach (var script in assemblyAsset.Scripts)
                ScriptLinker.RegisterAndLink(script, scriptByName, pending);

            context.AssemblyAssets.Add(assemblyAsset);
        }

        string refNote = skippedRefAssemblies > 0 ? $" ({skippedRefAssemblies} ref skipped)" : "";
        ConsoleReporter.Log("IL2Cpp",
            $"{totalAssemblies} assemblies, {totalTypes} types{refNote}");

        // CLEAN MEMORY AFTER STEP: Free large parser contexts, unused heaps, and raw binary/metadata bytes before CodeGen.
        context.Metadata?.ClearPostAssemblyMemory();
        context.MetadataBytes = Array.Empty<byte>();

        context.Registration?.ClearRelocations();

        context.Binary = null;
        context.BinaryBytes = Array.Empty<byte>();
        context.Disassembler = null;
        context.CallResolver = null;
        context.AddressMap = null;
        context.Bridge = null;
        context.FieldRvaResolver = null;
        context.Registration = null;
    }

    private static void FilterUnityEngineScript(ScriptAsset script)
    {
        script.Methods.Clear();
        script.Properties.Clear();
        script.Events.Clear();
        script.SkipMethodNames.Clear();

        foreach (var nested in script.NestedTypes)
        {
            FilterUnityEngineScript(nested);
        }
    }

    private static void AddStructsToAssemblyAsset(ScriptAsset script, AssemblyAsset assemblyAsset)
    {
        if (script.IsStruct)
        {
            assemblyAsset.Structs[script.FullName] = script;
        }
        foreach (var nested in script.NestedTypes)
        {
            AddStructsToAssemblyAsset(nested, assemblyAsset);
        }
    }

    // ─── Per-Script Analysis + AST (thread-safe: uses only thread-local tools) ──

    private static (int analyzed, int failed) ProcessScriptAnalysis(
        ScriptAsset script,
        Il2CppContext context,
        IrLifter? lifter,
        Thumb2IrLifter? thumb2Lifter,
        X64IrLifter? x64Lifter,
        bool isArm32,
        bool isX64,
        IrDataResolver dataResolver,
        IrCfgBuilder cfgBuilder,
        SsaBuilder ssaBuilder,
        SsaAstBuilder astBuilder,
        CapstoneDisassemblerWrapper? capstone = null)
    {
        int methodsAnalyzed = 0;
        int methodsFailed = 0;

        string typeName = script.FullName;

        foreach (var methodInfo in script.Methods)
        {
            int methIdx = methodInfo.GlobalIndex;
            if (!context.Bridge!.MethodAddressMap.TryGetValue(methIdx, out var va) || va == 0)
                continue;

            string retType = methodInfo.ReturnType;
            var paramParts = new List<string>();
            foreach (var p in methodInfo.Parameters)
                paramParts.Add($"{p.TypeName} {p.Name}");
            bool isStatic = methodInfo.IsStatic;

            string? prevMethod = ConsoleReporter.ActiveMethod;
            try
            {
                ConsoleReporter.ActiveMethod = methodInfo.Name;

                try
                {
                    // Step 1: Decode + Lift to flat IR (architecture-dependent)
                    Arm64Instruction[]? decodedArm64 = null;
                    Thumb2Instruction[]? decodedThumb2 = null;
                    IrMethod irMethod;
                    if (isArm32 && thumb2Lifter != null)
                    {
                        // Determine ARM vs Thumb mode from the raw pointer's bit 0
                        bool isThumbMethod = context.Bridge!.ThumbMethodAddresses.Contains(va);
                        
                        Thumb2Instruction[]? decoded;
                        if (isThumbMethod)
                        {
                            var result = context.Disassembler!.DecodeInstructionsThumb2(va);
                            if (!result.HasValue || result.Value.Instructions.Length == 0) continue;
                            decoded = result.Value.Instructions;
                        }
                        else
                        {
                            // ARM mode (bit 0 = 0): 4-byte fixed-width instructions
                            var result = context.Disassembler!.DecodeInstructionsArm32(va);
                            if (!result.HasValue || result.Value.Instructions.Length == 0) continue;
                            decoded = result.Value.Instructions;
                        }
                        decodedThumb2 = decoded;
                        
                        if (ConsoleReporter.IsTracing)
                        {
                            ConsoleReporter.Trace($"    [DEBUG] RAW {(isThumbMethod ? "THUMB2" : "ARM32")} INSTRUCTIONS FOR METHOD: {methodInfo.Name}");
                            foreach (var cInst in decoded)
                                ConsoleReporter.Trace($"      {cInst}");
                        }
                        
                        irMethod = thumb2Lifter.Lift(methodInfo.MethodDef, va, decoded,
                            methodInfo.Name, typeName, retType, paramParts, isStatic);
                    }
                    else if (isX64 && x64Lifter != null)
                    {
                        var decoded = context.Disassembler!.DecodeInstructionsX64(va);
                        if (!decoded.HasValue || decoded.Value.Instructions.Length == 0) continue;
                        
                        if (ConsoleReporter.IsTracing)
                        {
                            ConsoleReporter.Trace($"    [DEBUG] RAW X64 INSTRUCTIONS FOR METHOD: {methodInfo.Name}");
                            foreach (var cInst in decoded.Value.Instructions)
                                ConsoleReporter.Trace($"      {cInst}");
                        }
                        
                        irMethod = x64Lifter.Lift(methodInfo.MethodDef, va, decoded.Value.Instructions,
                            methodInfo.Name, typeName, retType, paramParts, isStatic);
                    }
                    else
                    {
                        var decoded = context.Disassembler!.DecodeInstructions(va);
                        if (!decoded.HasValue || decoded.Value.Instructions.Length == 0) continue;
                        
                        if (ConsoleReporter.IsTracing)
                        {
                            ConsoleReporter.Trace($"    [DEBUG] RAW ARM64 INSTRUCTIONS FOR METHOD: {methodInfo.Name}");
                            foreach (var cInst in decoded.Value.Instructions)
                                ConsoleReporter.Trace($"      {cInst}");
                        }
                        
                        irMethod = lifter!.Lift(methodInfo.MethodDef, va, decoded.Value.Instructions,
                            methodInfo.Name, typeName, retType, paramParts, isStatic);

                        // Preserve raw ARM64 data for Capstone comparison (only when --dump-ir)
                        if (Il2cppConfig.DumpIr && capstone != null)
                        {
                            decodedArm64 = decoded.Value.Instructions;
                        }
                    }

                    // Step 2: Resolve metadata
                    dataResolver.ResolveAll(irMethod);

                    if (ConsoleReporter.IsTracing)
                    {
                        ConsoleReporter.Trace($"    [DEBUG] RAW IR INSTRUCTIONS FOR METHOD: {methodInfo.Name}");
                        for (int i = 0; i < irMethod.Instructions.Count; i++)
                        {
                            var dbgInst = irMethod.Instructions[i];
                            ConsoleReporter.Trace($"      IR[{i:D3}] Addr=0x{dbgInst.Address:X} Op={dbgInst.Opcode} Ann=\"{dbgInst.Annotation ?? "null"}\"");
                            for (int s = 0; s < dbgInst.Sources.Length; s++)
                            {
                                var dbgSrc = dbgInst.Sources[s];
                                ConsoleReporter.Trace($"        Src[{s}]: Kind={dbgSrc.Kind} Val=0x{dbgSrc.Value:X} Offset=0x{dbgSrc.Offset:X} Bits={dbgSrc.BitWidth}");
                            }
                        }
                    }

                    // Step 3: IR noise reduction (skipped — reducer is a no-op)

                    // Step 3.5: Register state map (skipped — built but never queried)

                    // Step 4: Build CFG
                    var cfg = cfgBuilder.Build(irMethod);

                    // Step 5: CFG noise reduction (skipped — reducer is a no-op)
                    if (cfg != null)
                        SemanticCfgPruner.PruneClassInitFallbackEdges(cfg);

                    // Step 6: Build SSA
                    SsaContext? ssa = null;
                    if (cfg != null)
                    {
                        try { ssa = ssaBuilder.Build(cfg); }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[PIPELINE-ERROR] SSA Builder failed for method {methodInfo.Name} (VA=0x{va:X}): {ex.Message}");
                        }
                    }

                    // Step 7: Store analysis result
                    var analysisResult = new MethodAnalysisResult
                    {
                        MethodIndex = methIdx,
                        IrMethod = irMethod,
                        Cfg = cfg,
                        Ssa = ssa,
                    };

                    // Preserve raw ARM64 data for Capstone comparison
                    if (decodedArm64 != null && context.Binary != null)
                    {
                        analysisResult.RawArm64Instructions = decodedArm64;
                        analysisResult.MethodVA = va;
                        // Extract raw bytes for Capstone
                        long fileOff = context.Binary.VirtualToFileOffset(va);
                        if (fileOff >= 0)
                        {
                            int byteLen = decodedArm64.Length * 4;
                            if (fileOff + byteLen <= context.BinaryBytes.Length)
                            {
                                analysisResult.RawMethodBytes = new byte[byteLen];
                                Array.Copy(context.BinaryBytes, fileOff, analysisResult.RawMethodBytes, 0, byteLen);
                            }
                        }
                    }

                    // Preserve raw Thumb2 data for Capstone comparison
                    if (decodedThumb2 != null && context.Binary != null)
                    {
                        analysisResult.RawThumb2Instructions = decodedThumb2;
                        analysisResult.MethodVA = va;
                        // Track ARM vs Thumb mode for dump stage Capstone comparison
                        if (isArm32)
                            analysisResult.IsArmMode = !context.Bridge!.ThumbMethodAddresses.Contains(va);
                        // Extract raw bytes for Capstone
                        long fileOff = context.Binary.VirtualToFileOffset(va);
                        if (fileOff >= 0)
                        {
                            int byteLen = 0;
                            foreach (var ins in decodedThumb2)
                                byteLen += ins.Size;
                            if (fileOff + byteLen <= context.BinaryBytes.Length)
                            {
                                analysisResult.RawMethodBytes = new byte[byteLen];
                                Array.Copy(context.BinaryBytes, fileOff, analysisResult.RawMethodBytes, 0, byteLen);
                            }
                        }
                    }

                    methodInfo.AnalysisResult = analysisResult;

                    // Also store in global MethodResults for DumpStage compatibility
                    // ConcurrentDictionary: thread-safe indexer assignment
                    context.MethodResults[methIdx] = analysisResult;

                    // Step 8: SSA → AST
                    try
                    {
                        var ast = astBuilder.Build(analysisResult, script.Usings, context.TypeModel, context.FieldRvaResolver);
                        if (ast != null)
                        {
                            BoilerplatePruner.Prune(ast);
                            ExprSimplifier.Simplify(ast);
                            LoopClassifier.Classify(ast);
                            analysisResult.Ast = ast;
                            methodInfo.Ast = ast;
                        }
                        else
                        {
                            Console.WriteLine($"[PIPELINE-WARN] SsaAstBuilder returned null for method {methodInfo.Name} (VA=0x{va:X})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PIPELINE-ERROR] AST Builder failed for method {methodInfo.Name} (VA=0x{va:X}): {ex.Message}");
                    }

                    // Step 9: Free intermediate analysis data (only AST needed for codegen)
                    // This releases IrMethod, CFG, SSA objects — hundreds of MB across 69K methods
                    // Keep them if --dump-ir is active for DumpStage
                    if (!Il2cppConfig.DumpIr)
                    {
                        analysisResult.IrMethod = null!;
                        analysisResult.Cfg = null;
                        analysisResult.Ssa = null;
                    }

                    methodsAnalyzed++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PIPELINE-ERROR] Failed processing method {methodInfo.Name} (VA=0x{va:X}): {ex.Message}");
                    methodsFailed++;
                }
            }
            finally
            {
                ConsoleReporter.ActiveMethod = prevMethod;
            }
        }

        // Process nested type methods recursively
        foreach (var nested in script.NestedTypes)
        {
            var (nestedAnalyzed, nestedFailed) = ProcessScriptAnalysis(
                nested, context, lifter, thumb2Lifter, x64Lifter, isArm32, isX64, dataResolver,
                cfgBuilder, ssaBuilder, astBuilder, capstone);
            methodsAnalyzed += nestedAnalyzed;
            methodsFailed += nestedFailed;
        }

        return (methodsAnalyzed, methodsFailed);
    }
}
