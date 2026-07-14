using System.Collections.Generic;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Strips any MethodRef values that leaked through as call arguments.
/// These are IL2CPP internal MethodInfo* pointers, not valid C# arguments.
/// </summary>
public static class MethodRefStripper
{
    public static void Strip(List<ExprNode> args)
    {
        int originalCount = args.Count;
        args.RemoveAll(a => a is ExprVar mv && mv.Name.StartsWith("MethodRef("));
        if (args.Count != originalCount && ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"MethodRefStripper: Stripped {originalCount - args.Count} MethodRef arguments");
        }
    }
}
