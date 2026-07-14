using System;
using System.IO;
using System.Text;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Binary;
using Rosetta.Lifter.IR;

namespace Rosetta.Pipeline.Stages;

/// <summary>
/// Dumps IR, CFG, SSA, and def-use data to text files in a "Dumps/" directory
/// alongside the decompiled output. Each method gets its own file with all
/// intermediate representations, making it easy to trace the pipeline.
///
/// Activated by --dump-ir on the command line.
/// </summary>
public class DumpStage : IPipelineStage
{
    public string Name => "DUMP: Writing IR/CFG/SSA dumps to files";

    public void Execute(PipelineContext context)
    {
        if (!context.DumpIr) return;

        // Output directory: "Dumps/" relative to the decompiled output root
        string dumpDir = Path.Combine(context.Config.OutputDirectory, "Dumps");
        Directory.CreateDirectory(dumpDir);

        int filesWritten = 0;
        int totalDiscrepancies = 0;

        bool isArm32 = context.MethodResults.Values.FirstOrDefault()?.IrMethod?.IsArm32 == true;

        // Create disassembler wrappers for comparison (disposed at end)
        CapstoneDisassemblerWrapper? capstone = null;
        try { capstone = new CapstoneDisassemblerWrapper(isArm32); }
        catch { /* Capstone native library not available — comparison disabled */ }

        AsmArm64DisassemblerWrapper? asmArm64 = null;
        try { asmArm64 = new AsmArm64DisassemblerWrapper(); }
        catch { /* AsmArm64 not available */ }

        foreach (var (methIdx, result) in context.MethodResults)
        {
            var ir = result.IrMethod;
            if (ir == null) continue;
            string typeName = SanitizeFileName(ir.DeclaringType ?? "Unknown");
            string methodName = SanitizeFileName(ir.MethodName ?? $"Method_{methIdx}");

            // Group by type — each type gets one file with all its methods
            string typeDir = Path.Combine(dumpDir, typeName);
            Directory.CreateDirectory(typeDir);

            string filePath = Path.Combine(typeDir, $"{methodName}.dump.txt");

            var sb = new StringBuilder();

            // ═══════════════════════════════════════════════════════
            // Section 1: Method header
            // ═══════════════════════════════════════════════════════
            sb.AppendLine("╔═══════════════════════════════════════════════════════════════════╗");
            sb.AppendLine($"║  Method: {ir.ReturnType} {ir.DeclaringType}::{ir.MethodName}");
            sb.AppendLine($"║  Params: ({string.Join(", ", ir.Parameters)})");
            sb.AppendLine($"║  Address: 0x{ir.EntryAddress:X}  |  Static: {ir.IsStatic}");
            string archPrefix = ir.IsArm32 ? "ARM32" : "ARM64";
            sb.AppendLine($"║  {archPrefix} instructions: {ir.OriginalInstructionCount}  →  IR ops: {ir.Instructions.Count}");
            sb.AppendLine("╚═══════════════════════════════════════════════════════════════════╝");
            sb.AppendLine();

            // ═══════════════════════════════════════════════════════
            // Section 0: Capstone Comparison (before flat IR)
            // ═══════════════════════════════════════════════════════
            if (capstone != null && result.RawArm64Instructions != null && result.RawMethodBytes != null)
            {
                var comparison = CapstoneIrComparator.FormatComparison(
                    result.RawArm64Instructions,
                    result.RawMethodBytes,
                    result.MethodVA,
                    capstone,
                    methodName,
                    asmArm64);

                sb.AppendLine("══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════");
                sb.AppendLine("  SECTION 0a: DECODER DISCREPANCIES  (Custom vs Capstone vs AsmArm64)");
                sb.AppendLine("══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════");
                if (comparison != null)
                {
                    sb.AppendLine(comparison);
                    totalDiscrepancies++;
                }
                else
                {
                    sb.AppendLine($"  ✓ All {result.RawArm64Instructions.Length} instructions match.");
                }
                sb.AppendLine();

                // ═══════════════════════════════════════════════════════
                // Section 0b: Full side-by-side disassembly (3 decoders)
                // ═══════════════════════════════════════════════════════
                var csInsts = capstone.DisassembleBlock(result.RawMethodBytes, result.MethodVA);

                // Build address→Capstone map for O(1) lookup
                var csMap = new Dictionary<ulong, CapstoneInstruction>(csInsts.Length);
                foreach (var cs in csInsts)
                    csMap[cs.Address] = cs;

                // AsmArm64 disassembly (if available)
                AsmArm64Instruction[]? aaInsts = null;
                Dictionary<ulong, AsmArm64Instruction>? aaMap = null;
                if (asmArm64 != null)
                {
                    aaInsts = asmArm64.DisassembleBlock(result.RawMethodBytes, result.MethodVA);
                    aaMap = new Dictionary<ulong, AsmArm64Instruction>(aaInsts.Length);
                    foreach (var aa in aaInsts)
                        aaMap[aa.Address] = aa;
                }

                sb.AppendLine("══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════");
                sb.AppendLine("  SECTION 0b: FULL DISASSEMBLY  (Custom | Capstone | AsmArm64)");
                sb.AppendLine("══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════");
                sb.AppendLine($"  {"Address",-14} {"Raw",-12} {"Custom Decoder",-44} {"Capstone",-44} {"AsmArm64",-44}");
                sb.AppendLine($"  {"──────────",-14} {"────────",-12} {"──────────────────────────────────────────",-44} {"──────────────────────────────────────────",-44} {"──────────────────────────────────────────",-44}");

                foreach (var yours in result.RawArm64Instructions)
                {
                    string yourStr = yours.ToString();
                    string csStr = csMap.TryGetValue(yours.Address, out var csInst)
                        ? csInst.ToDisplayString()
                        : "—";
                    string aaStr = aaMap != null && aaMap.TryGetValue(yours.Address, out var aaInst)
                        ? aaInst.Text
                        : "—";

                    // Truncate for display
                    if (yourStr.Length > 42) yourStr = yourStr[..42] + "..";
                    if (csStr.Length > 42) csStr = csStr[..42] + "..";
                    if (aaStr.Length > 42) aaStr = aaStr[..42] + "..";

                    sb.AppendLine($"  0x{yours.Address:X8}    {yours.RawValue:X8}     {yourStr,-44} {csStr,-44} {aaStr,-44}");
                }
                sb.AppendLine();
            }

            if (capstone != null && result.RawThumb2Instructions != null && result.RawMethodBytes != null)
            {
                var comparison = CapstoneIrComparator.FormatComparison(
                    result.RawThumb2Instructions,
                    result.RawMethodBytes,
                    result.MethodVA,
                    capstone,
                    methodName,
                    result.IsArmMode);

                sb.AppendLine("══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════");
                sb.AppendLine("  SECTION 0a: DECODER DISCREPANCIES  (Custom vs Capstone)");
                sb.AppendLine("══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════");
                if (comparison != null)
                {
                    sb.AppendLine(comparison);
                    totalDiscrepancies++;
                }
                else
                {
                    sb.AppendLine($"  ✓ All {result.RawThumb2Instructions.Length} instructions match.");
                }
                sb.AppendLine();

                // ═══════════════════════════════════════════════════════
                // Section 0b: Full side-by-side disassembly
                // ═══════════════════════════════════════════════════════
                var csInsts = result.IsArmMode
                    ? capstone.DisassembleBlockArm(result.RawMethodBytes, result.MethodVA)
                    : capstone.DisassembleBlock(result.RawMethodBytes, result.MethodVA);

                // Build address→Capstone map for O(1) lookup
                var csMap = new Dictionary<ulong, CapstoneInstruction>(csInsts.Length);
                foreach (var cs in csInsts)
                    csMap[cs.Address] = cs;

                sb.AppendLine("══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════");
                sb.AppendLine("  SECTION 0b: FULL DISASSEMBLY  (Custom | Capstone)");
                sb.AppendLine("══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════");
                sb.AppendLine($"  {"Address",-14} {"Raw",-12} {"Custom Decoder",-44} {"Capstone",-44}");
                sb.AppendLine($"  {"──────────",-14} {"────────",-12} {"──────────────────────────────────────────",-44} {"──────────────────────────────────────────",-44}");

                foreach (var yours in result.RawThumb2Instructions)
                {
                    string yourStr = yours.ToString();
                    string csStr = csMap.TryGetValue(yours.Address, out var csInst)
                        ? csInst.ToDisplayString()
                        : "—";

                    // Truncate for display
                    if (yourStr.Length > 42) yourStr = yourStr[..42] + "..";
                    if (csStr.Length > 42) csStr = csStr[..42] + "..";

                    string rawHex = yours.Size == 4 ? $"{yours.RawValue:X8}" : $"{yours.RawValue & 0xFFFF:X4}";

                    sb.AppendLine($"  0x{yours.Address:X8}    {rawHex,-12} {yourStr,-44} {csStr,-44}");
                }
                sb.AppendLine();
            }

            // ═══════════════════════════════════════════════════════
            // Section 2: Flat IR (pre-CFG)
            // ═══════════════════════════════════════════════════════
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.AppendLine("  SECTION 1: FLAT IR  (output of IrLifter + DataResolver)");
            sb.AppendLine("════════════════════════════════════════════════════════════");
            foreach (var inst in ir.Instructions)
            {
                sb.AppendLine($"  [0x{inst.Address:X8}]  {inst}");
            }
            sb.AppendLine();

            // ═══════════════════════════════════════════════════════
            // Section 3: CFG
            // ═══════════════════════════════════════════════════════
            if (result.Cfg != null)
            {
                var cfg = result.Cfg;
                sb.AppendLine("════════════════════════════════════════════════════════════");
                sb.AppendLine($"  SECTION 2: CONTROL FLOW GRAPH  ({cfg.Blocks.Count} blocks, {cfg.EdgeCount} edges)");
                sb.AppendLine("════════════════════════════════════════════════════════════");

                foreach (var block in cfg.Blocks)
                {
                    // Block header
                    string preds = block.Predecessors.Count > 0
                        ? string.Join(", ", block.Predecessors.Select(e => $"BB{e.Source.Id}"))
                        : "(entry)";
                    string succs = block.Successors.Count > 0
                        ? string.Join(", ", block.Successors.Select(e => $"BB{e.Target.Id}"))
                        : "(exit)";

                    sb.AppendLine();
                    sb.AppendLine($"  ┌─── BB{block.Id} ───────────────────────────────────────");
                    sb.AppendLine($"  │  Address range: 0x{block.StartAddress:X} → 0x{block.EndAddress:X}");
                    sb.AppendLine($"  │  Predecessors: {preds}");
                    sb.AppendLine($"  │  Successors:   {succs}");
                    sb.AppendLine($"  │  Terminator:   {block.TerminatorKind}");
                    sb.AppendLine($"  │  Instructions: {block.Instructions.Count}");
                    sb.AppendLine($"  │");

                    // Instructions in block
                    foreach (var inst in block.Instructions)
                    {
                        sb.AppendLine($"  │  [0x{inst.Address:X8}]  {inst}");
                    }
                    sb.AppendLine($"  └────────────────────────────────────────────────────");
                }
                sb.AppendLine();
            }

            // ═══════════════════════════════════════════════════════
            // Section 4: SSA
            // ═══════════════════════════════════════════════════════
            if (result.Ssa != null && result.Cfg != null)
            {
                var ssa = result.Ssa;
                var cfg = result.Cfg;
                var defUse = new DefUseAnalyzer(ssa);

                sb.AppendLine("════════════════════════════════════════════════════════════");
                sb.AppendLine($"  SECTION 3: SSA  ({ssa.VariableCount} variables, {ssa.PhiCount} phis)");
                sb.AppendLine("════════════════════════════════════════════════════════════");

                // 4a: Phi nodes per block
                sb.AppendLine();
                sb.AppendLine("  ── Phi Nodes ──");
                foreach (var block in cfg.Blocks)
                {
                    var phis = ssa.GetPhis(block.Id);
                    if (phis.Count == 0) continue;

                    sb.AppendLine($"  BB{block.Id}:");
                    foreach (var phi in phis)
                    {
                        sb.AppendLine($"    {phi}");
                    }
                }

                // 4b: SSA operand map per block (instructions with SSA annotations)
                sb.AppendLine();
                sb.AppendLine("  ── SSA Annotated Instructions ──");
                foreach (var block in cfg.Blocks)
                {
                    sb.AppendLine($"  BB{block.Id}:");
                    foreach (var inst in block.Instructions)
                    {
                        var destSsa = ssa.GetDestination(inst.Address);
                        string destStr = destSsa.HasValue ? $"{destSsa.Value}" : "—";

                        var srcParts = new List<string>();
                        for (int s = 0; s < inst.Sources.Length; s++)
                        {
                            var srcSsa = ssa.GetSource(inst.Address, s);
                            srcParts.Add(srcSsa.HasValue ? $"{srcSsa.Value}" : $"({inst.Sources[s]})");
                        }
                        string srcsStr = srcParts.Count > 0 ? string.Join(", ", srcParts) : "";

                        sb.AppendLine($"    [0x{inst.Address:X8}]  {inst.Opcode,-20}  dst={destStr,-12}  src=[{srcsStr}]");
                        if (inst.Annotation != null)
                            sb.AppendLine($"                                              ; {inst.Annotation}");
                    }
                }

                // 4c: Def-Use chains
                sb.AppendLine();
                sb.AppendLine("  ── Def-Use Summary ──");
                foreach (var v in ssa.AllVariables)
                {
                    if (v.IsUndefined) continue;
                    int useCount = defUse.UseCount(v);
                    var def = defUse.GetDefinition(v);
                    string defStr = def.HasValue
                        ? (def.Value.instrIndex == -1 ? $"BB{def.Value.blockId} (phi)" : $"BB{def.Value.blockId}[{def.Value.instrIndex}]")
                        : "?";

                    var uses = defUse.GetUses(v);
                    int realUses = uses.Count(u => u.instrIndex >= 0);
                    int phiUses = uses.Count(u => u.instrIndex == -1);

                    sb.AppendLine($"    {v,-15}  uses={useCount} (real={realUses}, phi={phiUses})  def={defStr}");
                }

                sb.AppendLine();
            }

            try
            {
                File.WriteAllText(filePath, sb.ToString());
                filesWritten++;
            }
            catch
            {
                // ignore write failures for individual methods
            }
        }

        capstone?.Dispose();
        string discNote = totalDiscrepancies > 0 ? $" ({totalDiscrepancies} methods with discrepancies)" : " (all match)";
        ConsoleReporter.Success($"  {filesWritten} dump files written to: {Path.GetFullPath(dumpDir)}{discNote}");
    }

    private static string SanitizeFileName(string name)
    {
        // Remove invalid filename characters
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        // Trim dots and spaces from ends
        return name.Trim('.', ' ');
    }
}
