using System;
using System.Diagnostics;
using System.Collections.Generic;
using Rosetta.Pipeline;
using Rosetta.Pipeline.Stages;
// using Rosetta.Modules.TypeTree;
// using Rosetta.Interface;
// using Avalonia;

namespace Rosetta;

public static class Program
{
    public static int Main(string[] args)
    {
        var pipeline = new PipelineExecutor();
        var context  = new PipelineContext();

        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "scripts":
                    RunCodeGen(args[1..], pipeline, context);
                    return 1;
            }
        }

        // BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 1;
    }

    private static void RunCodeGen(string[] args, PipelineExecutor pipeline, PipelineContext context)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║        GAMERIPPER DECOMPILER — IL2CPP ARM64 v3.0          ║");
        Console.WriteLine("║    Vertical Pipeline · Per-Assembly · Deterministic        ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();

        context.args = args;

        pipeline.AddStage(new Il2cppStage());   // Full IL2CPP pipeline: load → resolve → analyze → AssemblyAssets
        pipeline.AddStage(new DumpStage());     // Optional: dump IR/CFG/SSA (only if --dump-ir)
        pipeline.AddStage(new CodeGenStage());  // Emit C# from AssemblyAssets

        pipeline.ExecuteAll(context);
        context.Timer.Stop();

        int totalMethods = 0;
        foreach (var asm in context.AssemblyAssets)
            totalMethods += asm.AnalyzedMethods;

        ConsoleReporter.Phase("COMPLETE", $"Total time: {context.Timer.Elapsed.TotalSeconds:F2}s | " +
            $"{context.AssemblyAssets.Count} assemblies, {totalMethods} methods");
    }
}