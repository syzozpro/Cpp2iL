using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Analysis.RegisterState;
using Rosetta.Lifter.IR;
using Rosetta.Metadata;
using Rosetta.Model;

namespace Rosetta.Pipeline.Stages;

/// <summary>
/// Unified analysis stage: processes each method through the full chain.
///
///   ARM64 → Lift(IrMethod) → DataResolve → IR Noise Reduce → RegisterStateMap
///         → Build CFG → CFG Noise Reduce → Build SSA → Store in Il2CppContext
///
/// This replaces the old disconnected dump stages (IrLiftStage, IrCfgStage,
/// SsaBuildStage, NoiseReductionStage) that each re-lifted from scratch.
///
/// Uses Parallel.ForEach with thread-local tools for concurrent type processing.
/// </summary>
public class MethodAnalyzer
{
    public void Run(Il2CppContext context)
    {
        if (context.Metadata == null || context.Binary == null ||
            context.Disassembler == null || context.CallResolver == null ||
            context.TypeResolver == null)
            throw new InvalidOperationException("Missing state for AnalysisStage");

        ConsoleReporter.Phase("ANALYSIS", "Processing methods: Lift → CFG → SSA");

        int methodsProcessed = 0;
        int methodsFailed = 0;

        // ── Build flat list of (typeIdx, TypeDefinition) to process ──────
        var typeWorkItems = new List<(int typeIdx, TypeDefinition td)>();

        for (int asmIdx = 0; asmIdx < context.Metadata.Assemblies.Length; asmIdx++)
        {
            var asm = context.Metadata.Assemblies[asmIdx];
            if (asm.ImageIndex < 0 || asm.ImageIndex >= context.Metadata.ImageDefinitions.Length)
                continue;

            string rawAsmName = asm.Name ?? $"Assembly_{asmIdx}";
            if (!string.IsNullOrEmpty(context.Config.TargetAssembly) &&
                !rawAsmName.Equals(context.Config.TargetAssembly, StringComparison.OrdinalIgnoreCase))
                continue;

            var img = context.Metadata.ImageDefinitions[asm.ImageIndex];
            int typeEnd = img.TypeStart + (int)img.TypeCount;
            if (typeEnd > context.Metadata.TypeDefinitions.Length)
                typeEnd = context.Metadata.TypeDefinitions.Length;

            for (int t = img.TypeStart; t < typeEnd; t++)
            {
                var td = context.Metadata.TypeDefinitions[t];
                if (td.DeclaringTypeIndex >= 0) continue; // skip nested, handled inline
                typeWorkItems.Add((t, td));
            }
        }

        // ── Process types in parallel with thread-local tools ────────────
        Parallel.ForEach(
            typeWorkItems,
            // Thread-local initializer: each thread gets its own analysis tools
            () => (
                Lifter: new IrLifter(context.CallResolver, context.Binary, context.BinaryBytes),
                DataResolver: new IrDataResolver(context.AddressMap, context.Metadata, context.Registration, context.TypeResolver, context.TypeModel),
                CfgBuilder: new IrCfgBuilder(),
                SsaBuilder: new SsaBuilder(),
                Processed: 0,
                Failed: 0
            ),
            // Body
            (workItem, loopState, localTools) =>
            {
                int localProcessed = 0;
                int localFailed = 0;

                ProcessType(context, workItem.td, workItem.typeIdx,
                    localTools.Lifter, localTools.DataResolver,
                    localTools.CfgBuilder, localTools.SsaBuilder,
                    ref localProcessed, ref localFailed);

                return (
                    localTools.Lifter,
                    localTools.DataResolver,
                    localTools.CfgBuilder,
                    localTools.SsaBuilder,
                    Processed: localTools.Processed + localProcessed,
                    Failed: localTools.Failed + localFailed
                );
            },
            // Final: accumulate per-thread totals
            localTools =>
            {
                Interlocked.Add(ref methodsProcessed, localTools.Processed);
                Interlocked.Add(ref methodsFailed, localTools.Failed);
            }
        );

        ConsoleReporter.Success($"  {methodsProcessed} methods analyzed ({methodsFailed} skipped)");
        ConsoleReporter.Success($"  {context.MethodResults.Count} method results stored in pipeline");
    }

    private void ProcessType(
        Il2CppContext ctx, TypeDefinition td, int typeIdx,
        IrLifter lifter, IrDataResolver dataResolver,
        IrCfgBuilder cfgBuilder, SsaBuilder ssaBuilder,
        ref int methodsProcessed, ref int methodsFailed)
    {
        string typeName = td.FullName ?? $"Type_{typeIdx}";

        for (int m = 0; m < td.MethodCount; m++)
        {
            int methIdx = td.MethodStart + m;
            if (methIdx < 0 || methIdx >= ctx.Metadata!.MethodDefinitions.Length) continue;

            var md = ctx.Metadata.MethodDefinitions[methIdx];
            if (!ctx.Bridge!.MethodAddressMap.TryGetValue(methIdx, out var va) || va == 0) continue;

            var decoded = ctx.Disassembler!.DecodeInstructions(va);
            if (!decoded.HasValue || decoded.Value.Instructions.Length == 0) continue;

            // Build method signature info
            string methodName = md.Name ?? $"Method_{methIdx}";
            string retType = md.ReturnTypeIndex >= 0 && ctx.TypeResolver != null
                ? ctx.TypeResolver.ResolveTypeName(md.ReturnTypeIndex) : "void";
            var paramParts = BuildParamList(ctx, md);
            bool isStatic = (md.Flags & 0x0010) != 0;

            try
            {
                // ═══════════════════════════════════════════════════════════
                // THE CHAIN — each step feeds into the next
                // ═══════════════════════════════════════════════════════════

                // Step 1: ARM64 → flat IR
                var irMethod = lifter.Lift(md, va, decoded.Value.Instructions,
                    methodName, typeName, retType, paramParts, isStatic);

                // Step 2: Resolve metadata (addresses, helpers, fields, etc.)
                dataResolver.ResolveAll(irMethod);

                // Step 3: Build forward register state map
                var regState = RegisterStateMap.Build(irMethod);

                // Step 4: Build CFG from IR
                var cfg = cfgBuilder.Build(irMethod);
                if (cfg != null)
                    SemanticCfgPruner.PruneClassInitFallbackEdges(cfg);

                // Step 6: Build SSA from cleaned CFG
                SsaContext? ssa = null;
                if (cfg != null)
                {
                    try
                    {
                        ssa = ssaBuilder.Build(cfg);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ANALYZER-ERROR] SSA Builder failed for method {methodName} (VA=0x{va:X}): {ex.Message}");
                    }
                }

                // Step 7: Store result — all downstream stages read from here
                var result = new MethodAnalysisResult
                {
                    MethodIndex = methIdx,
                    IrMethod = irMethod,
                    Cfg = cfg,
                    Ssa = ssa,
                    RegState = regState,
                };

                // ConcurrentDictionary: thread-safe indexer assignment
                ctx.MethodResults[methIdx] = result;
                methodsProcessed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ANALYZER-ERROR] Failed processing method {methodName} (VA=0x{va:X}): {ex.Message}");
                methodsFailed++;
            }
        }

        // Process nested types
        if (td.NestedTypeCount > 0)
        {
            for (int n = 0; n < td.NestedTypeCount; n++)
            {
                int nestedSlot = td.NestedTypesStart + n;
                if (nestedSlot < 0 || nestedSlot >= ctx.Metadata!.NestedTypes.Length) continue;
                int nestedIdx = ctx.Metadata.NestedTypes[nestedSlot];
                if (nestedIdx >= 0 && nestedIdx < ctx.Metadata.TypeDefinitions.Length)
                {
                    var nested = ctx.Metadata.TypeDefinitions[nestedIdx];
                    ProcessType(ctx, nested, nestedIdx, lifter, dataResolver,
                        cfgBuilder, ssaBuilder,
                        ref methodsProcessed, ref methodsFailed);
                }
            }
        }
    }

    private static List<string> BuildParamList(Il2CppContext ctx, MethodDefinition md)
    {
        var paramParts = new List<string>();
        for (int p = 0; p < md.ParameterCount; p++)
        {
            int pi = md.ParameterStart + p;
            if (pi >= 0 && pi < ctx.Metadata!.ParameterDefinitions.Length)
            {
                var pd = ctx.Metadata.ParameterDefinitions[pi];
                string pt = ctx.TypeResolver?.ResolveTypeName(pd.TypeIndex) ?? "?";
                string pn = pd.Name ?? $"p{p}";
                paramParts.Add($"{pt} {pn}");
            }
        }
        return paramParts;
    }
}
