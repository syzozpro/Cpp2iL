using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

public sealed partial class ExprPropagator
{
    /// <summary>
    /// Resolve the index register for a register-indexed array load.
    /// Traces back through the SSA chain to find the .Length variable and
    /// reconstruct the original index expression (e.g., arr.Length - 1).
    ///
    /// ARM64 pattern for arr[arr.Length - 1] on float[]:
    ///   w8 = arr.Length
    ///   x9 = 0xFFFFFFFF00000000        ; -1 << 32
    ///   x8 = ADD(x9, x8, LSL #32)     ; (length - 1) << 32
    ///   x8 = ASR(x8, 30)              ; (length - 1) * 4  (byte offset)
    /// </summary>
    private ExprNode ResolveArrayIndexFromRegister(IrInstruction inst, ExprNode baseExpr)
    {
        var ssaVar = _ssa.GetSource(inst.Address, 1);
        if (!ssaVar.HasValue)
            return GetSourceExpr(inst, 1);

        // Walk the SSA chain: index reg → SBFM → ADD → find the .Length operand
        var indexVar = ssaVar.Value;
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          ResolveArrayIndex: starting from {indexVar.Name} v{indexVar.Version}");

        // Step 1: Find the definition instruction for the index variable
        if (!_ssa.DefSites.TryGetValue(indexVar, out var defSite))
            return MakeVarExprOrFallback(indexVar);

        var defBlock = _cfg.FindBlock(defSite.blockId);
        if (defBlock == null || defSite.instrIndex < 0 || defSite.instrIndex >= defBlock.Instructions.Count)
            return MakeVarExprOrFallback(indexVar);

        var defInst = defBlock.Instructions[defSite.instrIndex];

        // Step 2: Check if the definition is BitfieldExtractSigned (ASR pattern)
        if (defInst.Opcode != IrOpcode.BitfieldExtractSigned)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          not SBFM, opcode={defInst.Opcode}");
            return MakeVarExprOrFallback(indexVar);
        }

        // Step 3: Get the SBFM's source — should be the ADD result
        var sbfmSrc = _ssa.GetSource(defInst.Address, 0);
        if (!sbfmSrc.HasValue)
            return MakeVarExprOrFallback(indexVar);

        // Step 4: Find the ADD instruction
        if (!_ssa.DefSites.TryGetValue(sbfmSrc.Value, out var addDefSite))
            return MakeVarExprOrFallback(indexVar);

        var addBlock = _cfg.FindBlock(addDefSite.blockId);
        if (addBlock == null || addDefSite.instrIndex < 0 || addDefSite.instrIndex >= addBlock.Instructions.Count)
            return MakeVarExprOrFallback(indexVar);

        var addInst = addBlock.Instructions[addDefSite.instrIndex];
        if (addInst.Opcode != IrOpcode.Add || addInst.Sources.Length < 2)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          not ADD, opcode={addInst.Opcode}");
            return MakeVarExprOrFallback(indexVar);
        }

        // Step 5: One of the ADD sources should trace back to .Length
        // The other source is the -1 mask (0xFFFFFFFF00000000)
        // Try both sources to find which one resolves to an array-related expression
        for (int si = 0; si < addInst.Sources.Length; si++)
        {
            var addSrcVar = _ssa.GetSource(addInst.Address, si);
            if (!addSrcVar.HasValue) continue;

            // Check if this source resolves to the .Length expression
            if (ExprMap.TryGetValue(addSrcVar.Value, out var srcExpr))
            {
                // Structural check: ExprField with FieldName "Length"
                if (srcExpr is ExprField ef && ef.FieldName == "Length")
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          found .Length source: {srcExpr.Emit()}");
                    var lengthExpr = Resolve(addSrcVar.Value);
                    return new ExprBinary("-", lengthExpr, new ExprLiteral(1));
                }
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          no .Length found in ADD sources");
        return MakeVarExprOrFallback(indexVar);
    }

    /// <summary>
    /// Make a variable expression, but ensure it's a variable that exists in the output.
    /// If the variable is inlined (single-use, won't be emitted as a statement),
    /// force-emit it as a statement in the current block.
    /// </summary>
    private ExprNode MakeVarExprOrFallback(SsaVariable v)
    {
        // If the variable is inlined, it won't appear as a statement.
        // In that case, use the resolved (inlined) expression directly.
        if (Inlined.Contains(v) && ExprMap.TryGetValue(v, out var inlinedExpr))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          MakeVarExprOrFallback: {v.Name} v{v.Version} is inlined, emitting directly");
            // Force un-inline: add a statement for this variable so it's declared
            if (_ctx.CurrentBlockId >= 0 && BlockStatements.TryGetValue(_ctx.CurrentBlockId, out var stmts))
            {
                var varExpr = MakeVarExpr(v);
                stmts.Add(new ExprStatement { Expr = new ExprAssign(varExpr, inlinedExpr), IsDeclaration = true, SsaVar = v });
                Inlined.Remove(v);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          force-emitted: var {varExpr.Emit()} = {inlinedExpr.Emit()}");
                return varExpr;
            }
            return inlinedExpr;
        }
        return MakeVarExpr(v);
    }

    /// <summary>
    /// Deduplicated helper to parse type(...) or typeof(...) annotations.
    /// </summary>
    private ExprNode? TryParseTypeOrTypeOf(string? ann, IrInstruction inst)
    {
        if (ann == null) return null;
        if (ann.StartsWith("typeof("))
        {
            var typeName = Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(ann[7..^1], _ctx.Usings);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → typeof({typeName})");
            return CreateTypeOfExpr(typeName, inst);
        }
        if (ann.StartsWith("type("))
        {
            var typeName = Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(ann[5..^1], _ctx.Usings);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → type({typeName})");
            var expr = new ExprVar($"type({typeName})");
            if (_typeModel != null && inst.MetadataIndex >= 0)
                expr.MetadataTypeDefIndex = _typeModel.ResolveTypeDefIndexFromTypeIndex(inst.MetadataIndex);
            return expr;
        }
        return null;
    }
}
