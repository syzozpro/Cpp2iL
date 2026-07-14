using System;
using Rosetta.Analysis.AST;
using Rosetta.Analysis.AST.Passes;
using Rosetta.Model;
namespace Rosetta.Pipeline.Stages;

/// <summary>
/// Pipeline stage that transforms analysis results into structured ASTs.
/// Uses the SSA-based SsaAstBuilder — no pattern matching.
/// </summary>
public class AstProcessor
{
    public void Run(Il2CppContext context)
    {
        ConsoleReporter.Phase("AST", "SSA expression propagation → AST");

        var builder = new SsaAstBuilder();
        int structured = 0;
        int failed = 0;

        foreach (var (methodIndex, result) in context.MethodResults)
        {
            try
            {
                var ast = builder.Build(result, null, context.TypeModel, context.FieldRvaResolver);
                if (ast != null)
                {
                    BoilerplatePruner.Prune(ast);
                    ExprSimplifier.Simplify(ast);
                    LoopClassifier.Classify(ast);
                    result.Ast = ast;
                    structured++;
                }
                else
                {
                    Console.WriteLine($"[AST-WARN] SsaAstBuilder returned null for method {result.IrMethod.MethodName} (VA=0x{result.IrMethod.EntryAddress:X})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AST-ERROR] AST processing failed for method {result.IrMethod.MethodName} (VA=0x{result.IrMethod.EntryAddress:X}): {ex.Message}");
                failed++;
            }
        }

        ConsoleReporter.Success($"  {structured} methods structured, {failed} failed");
    }
}
