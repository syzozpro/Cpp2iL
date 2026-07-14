using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Resolves stack pointer expressions (like local_spXX, ExprSpSlot, or SP arithmetic)
/// to their last stored stack slot values.
/// Used to resolve 'this' pointers or structure references in calls and boxing.
/// </summary>
public static class StackSlotResolver
{
    public static ExprNode ResolveThis(ExprNode thisExpr, PropagationContext ctx)
    {
        if (StackOffsetResolver.TryGetOffset(thisExpr, out long offset) &&
            ctx.StackSlotValues.TryGetValue(offset, out var storedValue))
        {
            // Detect IL2CPP fake-boxing (stack boxing of value types)
            if (storedValue is ExprTypeOf typeOfExpr && ctx.StackSlotValues.TryGetValue(offset + 0x10, out var boxedValue))
            {
                if (ConsoleReporter.IsTracing)
                    ConsoleReporter.Debug($"          this → fake-box resolved: {boxedValue.Emit()} (sp+0x{offset + 0x10:X})");
                
                var castExpr = new ExprCast(typeOfExpr.TypeName, boxedValue);
                castExpr.MetadataTypeDefIndex = typeOfExpr.MetadataTypeDefIndex;
                return castExpr;
            }

            if (ConsoleReporter.IsTracing)
                ConsoleReporter.Debug($"          this → stack-substituted: {storedValue.Emit()} (sp+0x{offset:X})");
            return storedValue;
        }

        return thisExpr;
    }
}
