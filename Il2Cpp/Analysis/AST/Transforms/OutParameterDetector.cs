using System.Collections.Generic;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;
using Rosetta.Analysis.Utils;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Detects out/ref parameters in calls. Any argument that resolves to a stack pointer
/// (SP + offset) is wrapped in an ExprOut, and the stack slot is tracked.
/// </summary>
public static class OutParameterDetector
{
    public static ExprNode Detect(ExprNode argExpr, Dictionary<SsaVariable, ExprNode> exprMap, 
        PropagationContext ctx, IReadOnlyList<Model.MethodSignature.ParamEntry>? parameters = null, int paramIndex = -1)
    {
        ExprNode? defExpr = argExpr;
        if (!ExprUtils.IsStackPointerExpr(defExpr))
        {
            if (argExpr is ExprVar ssaVar && ssaVar.VarId >= 0)
            {
                // BitWidth is ignored by SsaVariable's Equals/GetHashCode, so 0 is fine for lookup
                var ssaKey = new SsaVariable(ssaVar.VarId, ssaVar.Version, 0);
                if (exprMap.TryGetValue(ssaKey, out var mapExpr))
                    defExpr = mapExpr;
            }
        }
        if (ExprUtils.IsStackPointerExpr(defExpr))
        {
            long offset = ExprUtils.GetSpOffset(defExpr);
            string varName = $"local_sp{offset:X}";

            // METADATA CHECK: If we have parameter metadata, check if it's explicitly 'out', 'ref', or 'in'.
            // If it's a value type (like Vector3) without explicit reference modifiers,
            // back off and just emit the plain stack variable.
            if (parameters != null && paramIndex >= 0 && paramIndex < parameters.Count)
            {
                var paramEntry = parameters[paramIndex];
                if (!paramEntry.IsByRef)
                {
                    var plainVar = new ExprSpSlot(offset);
                    ctx.StackSlotValues[offset] = plainVar;
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          arg[{paramIndex}] → (metadata backed off) {plainVar.Emit()}");
                    return plainVar;
                }
                /*
                if (!string.IsNullOrEmpty(paramEntry.Name))
                {
                    // Use the original C# parameter name (e.g., 'hitInfo') instead of 'local_spXX'
                    varName = paramEntry.Name;
                    ctx.SpSlotNames[offset / 8] = varName;
                }
                */

                // Register the variable to be declared at the top of the method
                if (!ctx.OutVariableDeclarations.ContainsKey(varName))
                {
                    var kind = paramEntry.IsOut ? PropagationContext.VarDeclKind.Out
                             : paramEntry.IsIn  ? PropagationContext.VarDeclKind.In
                             :                    PropagationContext.VarDeclKind.Ref;

                    // For in/ref params: recover field initializers from stack stores.
                    // Phase 1 (IrLifter normalization) ensures store offsets and argument
                    // pointer offsets use the same post-frame reference, so direct lookup works.
                    ExprNode? initValue = null;
                    if (kind != PropagationContext.VarDeclKind.Out)
                    {
                        initValue = TryRecoverStructInit(ctx, paramEntry.TypeName, offset);
                    }

                    ctx.OutVariableDeclarations[varName] = new PropagationContext.VarDeclInfo(paramEntry.TypeName, kind, initValue);
                }

                // Emit 'out', 'in', or 'ref' based on the binary metadata attributes
                ExprNode newArg;
                if (paramEntry.IsOut)
                    newArg = new ExprOut(varName);
                else if (paramEntry.IsIn)
                    newArg = new ExprIn(varName);
                else
                    newArg = new ExprRef(varName);
                
                // Update stack slot so subsequent uses (e.g., hit.get_collider())
                // resolve to the out variable instead of the stale pre-call value
                ctx.StackSlotValues[offset] = new ExprVar(varName);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          arg[{paramIndex}] → {(paramEntry.IsOut ? "out" : paramEntry.IsIn ? "in" : "ref")} {varName}");
                return newArg;
            }

            // Fallback if no parameters
            if (!ctx.OutVariableDeclarations.ContainsKey(varName))
            {
                ctx.OutVariableDeclarations[varName] = new PropagationContext.VarDeclInfo("var", PropagationContext.VarDeclKind.Unknown);
            }
            var fallbackArg = new ExprOut(varName);
            ctx.StackSlotValues[offset] = new ExprVar(varName);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          arg[{paramIndex}] → out {varName}");
            return fallbackArg;
        }
        return argExpr;
    }

    /// <summary>
    /// Tries to recover a struct initializer from stack stores using FieldLayout metadata.
    /// For a struct at stack base <paramref name="baseOffset"/>:
    ///   - Looks up the type's FieldLayout to get field names and metadata offsets
    ///   - Subtracts the IL2CPP object header (16 bytes) to get stack-relative offsets
    ///   - Checks StackSlotValues[baseOffset + fieldStackOffset] for each field
    ///   - Returns ExprStructInit { field1 = val1, ... } or null if no stores found
    /// </summary>
    private static ExprNode? TryRecoverStructInit(PropagationContext ctx, string typeName, long baseOffset)
    {
        var typeModel = ctx.TypeModel;
        if (typeModel == null) return null;

        // Strip in/ref/out/& prefixes to get the bare type name
        string bare = Rosetta.Common.TypeUtils.StripModifiers(typeName);



        // Look up the FieldLayout for this type — O(1)
        if (!typeModel.FieldLayoutsByTypeName.TryGetValue(bare, out int typeDefIndex))
        {
            return null;
        }
        if (!typeModel.FieldLayouts.TryGetValue(typeDefIndex, out var layout))
            return null;



        // IL2CPP object header size (16 bytes on 64-bit). Instance field offsets
        // in metadata include this header, but stack-allocated structs don't have it.
        const int ObjectHeaderSize = 16;

        var structInit = new ExprStructInit(
            Rosetta.Analysis.Utils.StringUtils.CleanTypeName(bare));

        foreach (var field in layout.Fields)
        {
            if (field.IsStatic) continue;

            // Convert metadata offset → stack offset relative to struct base
            long fieldStackOffset = field.Offset - ObjectHeaderSize;
            long storeOffset = baseOffset + fieldStackOffset;



            if (ctx.StackSlotValues.TryGetValue(storeOffset, out var storedVal)
                && storedVal is not ExprVar) // skip if already a named variable
            {

                structInit.Fields.Add((field.Name, storedVal));
            }
        }


        // Return the struct init only if we recovered at least one field
        if (structInit.Fields.Count > 0)
        {
            if (ConsoleReporter.Verbose)
            {
                ConsoleReporter.Debug($"OutParameterDetector: Recovered struct initializer for '{bare}' at SP offset {baseOffset} containing {structInit.Fields.Count} fields");
            }
            return structInit;
        }
        return null;
    }
}
