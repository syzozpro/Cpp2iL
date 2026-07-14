using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Config;

namespace Rosetta.Analysis.AST.Passes;

/// <summary>
/// Post-processing AST pass that classifies while loops into more specific
/// loop constructs (for, foreach) based on structural pattern matching.
///
/// Operates on emitted AST after SsaAstBuilder, BoilerplatePruner, and
/// ExprSimplifier have run. Does not modify semantics, only syntax.
///
/// Recognizes:
///   - Counted for-loops: while(true) with init/increment/break-condition
///   - Enumerator foreach: GetEnumerator → while(true) { MoveNext + break } → Dispose
/// </summary>
public static class LoopClassifier
{
    /// <summary>
    /// Classify while loops in the given method into for/foreach where possible.
    /// </summary>
    public static void Classify(AstMethod method)
    {
        if (Il2cppConfig.DisableLoopReconstructure) return;
        ClassifyBlock(method.Body);
    }

    /// <summary>
    /// Recursively walk a block and classify while loops.
    /// </summary>
    private static void ClassifyBlock(AstBlock block)
    {
        for (int i = 0; i < block.Statements.Count; i++)
        {
            var stmt = block.Statements[i];

            if (stmt is AstWhileStatement wh)
            {
                if (TryClassifyForLoop(block, ref i, wh))
                {
                    if (block.Statements[i] is AstForStatement fs)
                        ClassifyBlock(fs.Body);
                    continue;
                }

                if (TryClassifyEnumeratorForeach(block, ref i, wh))
                {
                    if (block.Statements[i] is AstForeachStatement fe2)
                        ClassifyBlock(fe2.Body);
                    continue;
                }

                // Not classified — recurse into the while body
                ClassifyBlock(wh.Body);
            }
            else if (stmt is AstIfStatement ifs)
            {
                ClassifyBlock(ifs.ThenBody);
                if (ifs.ElseBody != null)
                    ClassifyBlock(ifs.ElseBody);
            }
            else if (stmt is AstForStatement existingFor)
            {
                ClassifyBlock(existingFor.Body);
            }
            else if (stmt is AstForeachStatement existingFe)
            {
                ClassifyBlock(existingFe.Body);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // For-Loop Classification
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Try to classify a while(true) loop as a for-loop.
    ///
    /// Matches the pattern:
    ///   [parent block]:
    ///     type counter = init;                  // init declaration
    ///     while (true) {
    ///       ... body ...
    ///       temp = counter + step;              // optional temp declaration
    ///       counter = temp;                     // increment (or direct: counter = counter + step)
    ///       if (temp/counter OP limit) { break; }
    ///     }
    ///
    /// Transforms to:
    ///     for (type counter = init; counter NEG_OP limit; counter = counter + step)
    ///     {
    ///       ... body ...
    ///     }
    ///
    /// Safety: refuses to transform if the counter variable is used after the loop
    /// (scope change) or if the body contains continue statements (semantic change).
    /// </summary>
    private static bool TryClassifyForLoop(AstBlock parentBlock, ref int whileIndex, AstWhileStatement wh)
    {
        // Requirement: while(true)
        if (wh.Condition is not AstLiteral lit || lit.Value is not bool bv || !bv)
            return false;

        var body = wh.Body.Statements;
        if (body.Count < 2) return false;

        // A continue inside the body would change semantics in a for-loop:
        // in while(true), continue skips the increment; in for, it executes it.
        if (ContainsContinue(wh.Body))
            return false;

        // Last statement must be: if (<cond>) { break; }
        if (body[body.Count - 1] is not AstIfStatement breakIf) return false;
        if (breakIf.ElseBody != null) return false;
        if (breakIf.ThenBody.Statements.Count != 1) return false;
        if (breakIf.ThenBody.Statements[0] is not AstBreakStatement) return false;

        // Break condition must be a binary comparison
        if (breakIf.Condition is not AstBinaryExpression breakCond) return false;
        if (!IsComparisonOp(breakCond.Operator)) return false;

        // Extract the increment pattern from before the break
        if (!TryExtractIncrement(body, body.Count - 2,
                out string counterVar, out string? tempVar,
                out int incrementStmtCount, out AstNode updateStmt))
            return false;

        // Verify the break condition references the counter or temp variable
        string? condLeftVar = (breakCond.Left is AstIdentifier lid) ? lid.Name : null;
        string? condRightVar = (breakCond.Right is AstIdentifier rid) ? rid.Name : null;

        bool condUsesCounterOrTemp = (condLeftVar == counterVar || condLeftVar == tempVar ||
                                      condRightVar == counterVar || condRightVar == tempVar);
        if (!condUsesCounterOrTemp) return false;

        // Find the counter initialization in the parent block (search backwards)
        if (!TryFindCounterInit(parentBlock, whileIndex, counterVar,
                out int initIndex, out AstVariableDeclaration initDecl))
            return false;

        // Verify counter is NOT used after the while loop in the parent block
        // (hoisting into for-init would change the scope)
        if (IsVariableUsedAfter(parentBlock, whileIndex, counterVar))
            return false;

        // ─── All checks passed — transform ───

        // Build the for-loop condition by negating the break condition
        AstExpression forCondition = BuildForCondition(breakCond, tempVar, counterVar);

        // Build the for body (strip increment statements + break from the end)
        var forBody = new AstBlock();
        int bodyEndExclusive = body.Count - 1 - incrementStmtCount;
        for (int j = 0; j < bodyEndExclusive; j++)
            forBody.Statements.Add(body[j]);

        // Create the for statement
        var forStmt = new AstForStatement
        {
            Init = initDecl,
            Condition = forCondition,
            Update = updateStmt,
            Body = forBody
        };

        // Remove the init declaration from the parent block
        parentBlock.Statements.RemoveAt(initIndex);
        if (initIndex < whileIndex) whileIndex--;

        // Replace the while with the for
        parentBlock.Statements[whileIndex] = forStmt;
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Increment Extraction
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Try to extract a counter increment pattern from the body,
    /// searching at the given position (just before the trailing break).
    ///
    /// Pattern 1 (with temp): 2 statements
    ///   AstVariableDeclaration: temp = counter + step
    ///   AstExpressionStatement: counter = temp
    ///
    /// Pattern 2 (direct): 1 statement
    ///   AstExpressionStatement: counter = counter + step
    /// </summary>
    private static bool TryExtractIncrement(
        List<AstNode> body, int searchEnd,
        out string counterVar, out string? tempVar,
        out int stmtCount, out AstNode updateStmt)
    {
        counterVar = ""; tempVar = null; stmtCount = 0; updateStmt = null!;

        if (searchEnd < 0) return false;

        // ─── Pattern 1: temp = counter + step; counter = temp ───
        if (searchEnd >= 1)
        {
            var assignStmt = body[searchEnd];
            var tempDeclStmt = body[searchEnd - 1];

            if (assignStmt is AstExpressionStatement es
                && es.Expression is AstAssignment assign
                && assign.Target is AstIdentifier assignTarget
                && assign.Value is AstIdentifier assignValue
                && tempDeclStmt is AstVariableDeclaration vd
                && vd.VarName == assignValue.Name
                && vd.Initializer is AstBinaryExpression addExpr
                && IsArithmeticOp(addExpr.Operator)
                && addExpr.Left is AstIdentifier addLeft
                && addLeft.Name == assignTarget.Name
                && addExpr.Right is AstLiteral stepLit
                && IsNumeric(stepLit.Value))
            {
                counterVar = assignTarget.Name;
                tempVar = vd.VarName;
                stmtCount = 2;
                updateStmt = BuildUpdateStatement(counterVar, addExpr.Operator, addExpr.Right);
                return true;
            }
        }

        // ─── Pattern 2: counter = counter + step ───
        if (body[searchEnd] is AstExpressionStatement es2
            && es2.Expression is AstAssignment assign2
            && assign2.Target is AstIdentifier target2
            && assign2.Value is AstBinaryExpression addExpr2
            && IsArithmeticOp(addExpr2.Operator)
            && addExpr2.Left is AstIdentifier addLeft2
            && addLeft2.Name == target2.Name
            && addExpr2.Right is AstLiteral stepLit2
            && IsNumeric(stepLit2.Value))
        {
            counterVar = target2.Name;
            tempVar = null;
            stmtCount = 1;
            updateStmt = BuildUpdateStatement(counterVar, addExpr2.Operator, addExpr2.Right);
            return true;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Counter Initialization Finder
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Search backwards in the parent block for a variable declaration
    /// that initializes the counter variable. Searches within a small
    /// window (5 statements) to avoid false matches.
    /// </summary>
    private static bool TryFindCounterInit(
        AstBlock parentBlock, int whileIndex, string counterVar,
        out int initIndex, out AstVariableDeclaration initDecl)
    {
        initIndex = -1;
        initDecl = null!;

        int searchStart = Math.Max(0, whileIndex - 5);
        for (int i = whileIndex - 1; i >= searchStart; i--)
        {
            if (parentBlock.Statements[i] is AstVariableDeclaration vd
                && vd.VarName == counterVar
                && vd.Initializer != null)
            {
                initIndex = i;
                initDecl = vd;
                return true;
            }
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Condition Building
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build the for-loop condition by negating the break condition
    /// and substituting the temp variable with the counter variable.
    ///
    /// Example: break condition "x8_15 >= 5" with temp="x8_15", counter="i"
    ///          produces for-condition "i &lt; 5"
    /// </summary>
    private static AstExpression BuildForCondition(
        AstBinaryExpression breakCond, string? tempVar, string counterVar)
    {
        string negatedOp = NegateComparisonOp(breakCond.Operator);

        AstExpression left = SubstituteVar(breakCond.Left, tempVar, counterVar);
        AstExpression right = SubstituteVar(breakCond.Right, tempVar, counterVar);

        return new AstBinaryExpression
        {
            Operator = negatedOp,
            Left = left,
            Right = right
        };
    }

    /// <summary>
    /// If the expression is an identifier matching oldName, replace with newName.
    /// Otherwise return the expression unchanged.
    /// </summary>
    private static AstExpression SubstituteVar(AstExpression expr, string? oldName, string newName)
    {
        if (oldName != null && expr is AstIdentifier id && id.Name == oldName)
            return new AstIdentifier { Name = newName };
        return expr;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Post-Loop Variable Usage Check
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Check if a variable is referenced in any statement AFTER the given index
    /// in the parent block. Used to ensure it's safe to hoist a variable
    /// into a for-loop init (which would change its scope).
    /// </summary>
    private static bool IsVariableUsedAfter(AstBlock block, int afterIndex, string varName)
    {
        for (int i = afterIndex + 1; i < block.Statements.Count; i++)
        {
            if (ContainsIdentifier(block.Statements[i], varName))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Recursively check if an AST node contains an identifier with the given name.
    /// </summary>
    private static bool ContainsIdentifier(AstNode node, string name)
    {
        if (node is AstIdentifier id)
            return id.Name == name;
        if (node is AstBlock block)
            return block.Statements.Any(s => ContainsIdentifier(s, name));
        if (node is AstIfStatement ifs)
            return ContainsIdentifier(ifs.Condition, name) ||
                   ContainsIdentifier(ifs.ThenBody, name) ||
                   (ifs.ElseBody != null && ContainsIdentifier(ifs.ElseBody, name));
        if (node is AstWhileStatement wh)
            return ContainsIdentifier(wh.Condition, name) || ContainsIdentifier(wh.Body, name);
        if (node is AstForStatement fs)
            return (fs.Init != null && ContainsIdentifier(fs.Init, name)) ||
                   (fs.Condition != null && ContainsIdentifier(fs.Condition, name)) ||
                   (fs.Update != null && ContainsIdentifier(fs.Update, name)) ||
                   ContainsIdentifier(fs.Body, name);
        if (node is AstForeachStatement fe)
            return fe.ItemName == name || fe.CollectionName == name || ContainsIdentifier(fe.Body, name);
        if (node is AstReturnStatement ret)
            return ret.Value != null && ContainsIdentifier(ret.Value, name);
        if (node is AstExpressionStatement es)
            return ContainsIdentifier(es.Expression, name);
        if (node is AstVariableDeclaration vd)
            return vd.VarName == name || (vd.Initializer != null && ContainsIdentifier(vd.Initializer, name));
        if (node is AstAssignment assign)
            return ContainsIdentifier(assign.Target, name) || ContainsIdentifier(assign.Value, name);
        if (node is AstBinaryExpression bin)
            return ContainsIdentifier(bin.Left, name) || ContainsIdentifier(bin.Right, name);
        if (node is AstUnaryExpression unary)
            return ContainsIdentifier(unary.Operand, name);
        if (node is AstCallExpression call)
            return (call.Target != null && ContainsIdentifier(call.Target, name)) ||
                   call.Arguments.Any(a => ContainsIdentifier(a, name));
        if (node is AstMemberAccess ma)
            return ContainsIdentifier(ma.Target, name);
        if (node is AstIndexAccess idx)
            return ContainsIdentifier(idx.Target, name) || ContainsIdentifier(idx.Index, name);
        if (node is AstCastExpression cast)
            return ContainsIdentifier(cast.Operand, name);
        if (node is AstNewExpression ne)
            return ne.Arguments.Any(a => ContainsIdentifier(a, name));
        if (node is AstMemoryAccess mem)
            return ContainsIdentifier(mem.Base, name);
        if (node is AstLiteral)
            return false;
        if (node is AstBreakStatement || node is AstContinueStatement)
            return false;

        return false;
    }

    /// <summary>
    /// Check if a block (or any nested non-loop block) contains a continue statement.
    /// Does NOT recurse into nested loops — their continues are for the inner loop.
    /// </summary>
    private static bool ContainsContinue(AstNode node)
    {
        if (node is AstContinueStatement) return true;
        if (node is AstBlock block)
            return block.Statements.Any(s => ContainsContinue(s));
        if (node is AstIfStatement ifs)
            return ContainsContinue(ifs.ThenBody) ||
                   (ifs.ElseBody != null && ContainsContinue(ifs.ElseBody));
        // Stop at nested loops — their continues don't affect the outer loop
        if (node is AstWhileStatement || node is AstForStatement || node is AstForeachStatement)
            return false;
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Enumerator Foreach Classification
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Try to classify a while(true) loop as a foreach loop using the
    /// enumerator pattern: GetEnumerator → MoveNext + break → Dispose.
    ///
    /// Matches:
    ///   [parent block]:
    ///     enumerator = collection.GetEnumerator();
    ///     while (true) {
    ///       bool result = enumerator.MoveNext();
    ///       if ((result & 1) == 0) { break; }  // or if (!result) { break; }
    ///       ... body ...
    ///     }
    ///     enumerator.Dispose();  // optional
    ///
    /// Transforms to:
    ///     foreach (var item in collection) { ... body ... }
    /// </summary>
    private static bool TryClassifyEnumeratorForeach(AstBlock parentBlock, ref int whileIndex, AstWhileStatement wh)
    {
        // Requirement: while(true)
        if (wh.Condition is not AstLiteral lit || lit.Value is not bool bv || !bv)
            return false;

        var body = wh.Body.Statements;
        if (body.Count < 2) return false;

        // Body[0] must be: <bool_var> = <enumerator>.MoveNext()
        if (!TryExtractMoveNext(body[0], out string moveNextResultVar, out string enumeratorVar))
            return false;

        // Body[1] must be: if (<falsy check on moveNextResultVar>) { break; }
        if (!IsFalsyBreakCheck(body[1], moveNextResultVar))
            return false;

        // Look for GetEnumerator() call before the while in the parent block
        if (!TryFindGetEnumerator(parentBlock, whileIndex, enumeratorVar,
                out int getEnumIndex, out string collectionName))
            return false;

        // Look for Dispose() call after the while (optional, part of foreach pattern)
        int disposeIndex = TryFindDispose(parentBlock, whileIndex);

        // ─── All checks passed — transform ───

        // Build the foreach body (skip MoveNext decl + break-check)
        var foreachBody = new AstBlock();
        for (int j = 2; j < body.Count; j++)
            foreachBody.Statements.Add(body[j]);

        // Extract the item variable name from get_Current()/Current access in the body,
        // falling back to the enumerator variable name (always data-derived, never hardcoded)
        string itemName = ExtractItemName(foreachBody, enumeratorVar);

        // Create foreach statement
        var foreachStmt = new AstForeachStatement
        {
            ItemName = itemName,
            CollectionName = collectionName,
            Body = foreachBody
        };

        // Remove Dispose (if found — must remove BEFORE GetEnumerator to keep indices valid)
        if (disposeIndex >= 0)
        {
            parentBlock.Statements.RemoveAt(disposeIndex);
            // disposeIndex is always after whileIndex, so whileIndex stays valid
        }

        // Remove GetEnumerator statement
        parentBlock.Statements.RemoveAt(getEnumIndex);
        if (getEnumIndex < whileIndex) whileIndex--;

        // Replace the while with the foreach
        parentBlock.Statements[whileIndex] = foreachStmt;
        return true;
    }

    /// <summary>
    /// Extract the MoveNext call from the first statement of the loop body.
    ///
    /// Patterns:
    ///   - AstVariableDeclaration { Initializer = call.MoveNext() }
    ///   - AstExpressionStatement { AstAssignment { Value = call.MoveNext() } }
    /// </summary>
    private static bool TryExtractMoveNext(AstNode stmt, out string resultVar, out string enumeratorVar)
    {
        resultVar = ""; enumeratorVar = "";

        // Pattern 1: bool result = enumerator.MoveNext()
        if (stmt is AstVariableDeclaration vd
            && vd.Initializer is AstCallExpression call
            && call.MethodName.Contains("MoveNext"))
        {
            resultVar = vd.VarName;
            enumeratorVar = ExtractCallReceiver(call);
            return enumeratorVar.Length > 0;
        }

        // Pattern 2: result = enumerator.MoveNext()
        if (stmt is AstExpressionStatement es
            && es.Expression is AstAssignment assign
            && assign.Target is AstIdentifier assignTarget
            && assign.Value is AstCallExpression call2
            && call2.MethodName.Contains("MoveNext"))
        {
            resultVar = assignTarget.Name;
            enumeratorVar = ExtractCallReceiver(call2);
            return enumeratorVar.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// Extract the receiver expression from a call as a string.
    /// Handles identifiers, member access, and index access.
    /// </summary>
    private static string ExtractCallReceiver(AstCallExpression call)
    {
        AstExpression? receiver = call.Target;
        if (receiver == null && call.Arguments.Count > 0)
            receiver = call.Arguments[0];
        if (receiver == null) return "";
        return EmitReceiver(receiver);
    }

    /// <summary>
    /// Emit an expression as a string for use in foreach headers.
    /// Handles the common sub-expression types structurally.
    /// </summary>
    private static string EmitReceiver(AstExpression expr) => expr switch
    {
        AstIdentifier id => id.Name,
        AstMemberAccess ma => $"{EmitReceiver(ma.Target)}.{ma.MemberName}",
        AstIndexAccess ia => $"{EmitReceiver(ia.Target)}[{EmitReceiver(ia.Index)}]",
        AstLiteral lit => lit.Value?.ToString() ?? "null",
        _ => expr.ToString() ?? "?"
    };

    /// <summary>
    /// Check if a statement is: if (&lt;falsy test on resultVar&gt;) { break; }
    /// </summary>
    private static bool IsFalsyBreakCheck(AstNode stmt, string resultVar)
    {
        if (stmt is not AstIfStatement ifs) return false;
        if (ifs.ElseBody != null) return false;
        if (ifs.ThenBody.Statements.Count != 1) return false;
        if (ifs.ThenBody.Statements[0] is not AstBreakStatement) return false;

        return IsFalsyTest(ifs.Condition, resultVar);
    }

    /// <summary>
    /// Check if an expression is a "falsy" test of a variable.
    ///
    /// Recognized patterns:
    ///   - (varName & 1) == 0    (IL2CPP boolean check)
    ///   - varName == 0
    ///   - varName == false
    ///   - !varName
    /// </summary>
    private static bool IsFalsyTest(AstExpression cond, string varName)
    {
        // Pattern: (varName & 1) == 0
        if (cond is AstBinaryExpression bin1 && bin1.Operator == "=="
            && bin1.Left is AstBinaryExpression andExpr && andExpr.Operator == "&"
            && andExpr.Left is AstIdentifier andId && andId.Name == varName
            && andExpr.Right is AstLiteral andLit && IsIntOne(andLit.Value)
            && bin1.Right is AstLiteral zeroLit && IsIntZero(zeroLit.Value))
            return true;

        // Pattern: varName == 0
        if (cond is AstBinaryExpression bin2 && bin2.Operator == "=="
            && bin2.Left is AstIdentifier id2 && id2.Name == varName
            && bin2.Right is AstLiteral zeroLit2 && IsIntZero(zeroLit2.Value))
            return true;

        // Pattern: varName == false
        if (cond is AstBinaryExpression bin3 && bin3.Operator == "=="
            && bin3.Left is AstIdentifier id3 && id3.Name == varName
            && bin3.Right is AstLiteral falseLit && falseLit.Value is bool fb && !fb)
            return true;

        // Pattern: !varName
        if (cond is AstUnaryExpression unary && unary.Operator == "!"
            && unary.Operand is AstIdentifier unaryId && unaryId.Name == varName)
            return true;

        return false;
    }

    /// <summary>
    /// Search backwards in the parent block for a GetEnumerator() call
    /// that assigns to the enumerator variable. Traces through intermediate
    /// assignment chains (e.g., enu1 = coll.GetEnumerator(); enu = enu1).
    /// </summary>
    private static bool TryFindGetEnumerator(AstBlock parentBlock, int whileIndex, string enumeratorVar,
        out int getEnumIndex, out string collectionName)
    {
        getEnumIndex = -1; collectionName = "";

        string currentVar = enumeratorVar;
        int searchStart = Math.Max(0, whileIndex - 10);

        for (int i = whileIndex - 1; i >= searchStart; i--)
        {
            var stmt = parentBlock.Statements[i];

            // Pattern: EnumType currentVar = collection.GetEnumerator()
            if (stmt is AstVariableDeclaration vd && vd.VarName == currentVar)
            {
                if (vd.Initializer is AstCallExpression call && call.MethodName.Contains("GetEnumerator"))
                {
                    collectionName = ExtractCallReceiver(call);
                    if (collectionName.Length > 0)
                    {
                        getEnumIndex = i;
                        return true;
                    }
                }
                // Trace through: EnumType enu1 = sourceVar → follow sourceVar
                if (vd.Initializer is AstIdentifier sourceId)
                {
                    currentVar = sourceId.Name;
                    continue;
                }
            }

            // Pattern: currentVar = collection.GetEnumerator()
            if (stmt is AstExpressionStatement es
                && es.Expression is AstAssignment assign
                && assign.Target is AstIdentifier assignTarget
                && assignTarget.Name == currentVar)
            {
                if (assign.Value is AstCallExpression call2 && call2.MethodName.Contains("GetEnumerator"))
                {
                    collectionName = ExtractCallReceiver(call2);
                    if (collectionName.Length > 0)
                    {
                        getEnumIndex = i;
                        return true;
                    }
                }
                // Trace through: enu = enu1 → follow enu1
                if (assign.Value is AstIdentifier sourceId2)
                {
                    currentVar = sourceId2.Name;
                    continue;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Extract the foreach item variable name from the loop body.
    /// Searches for get_Current() / .Current access on the enumerator.
    /// Falls back to the enumerator variable name (always data-derived).
    /// </summary>
    private static string ExtractItemName(AstBlock body, string enumeratorVar)
    {
        foreach (var stmt in body.Statements)
        {
            // Pattern: var x = enumerator.get_Current() or enumerator.Current
            if (stmt is AstVariableDeclaration vd
                && vd.Initializer != null
                && IsCurrentAccess(vd.Initializer, enumeratorVar))
            {
                return vd.VarName;
            }

            // Pattern: x = enumerator.get_Current()
            if (stmt is AstExpressionStatement es
                && es.Expression is AstAssignment assign
                && assign.Target is AstIdentifier target
                && IsCurrentAccess(assign.Value, enumeratorVar))
            {
                return target.Name;
            }
        }

        // No get_Current found — use the enumerator variable name (data-derived)
        return enumeratorVar;
    }

    /// <summary>
    /// Check if an expression accesses the Current element of an enumerator.
    /// </summary>
    private static bool IsCurrentAccess(AstExpression expr, string enumeratorVar)
    {
        // Pattern: enumerator.get_Current() or enumerator.Current (as call)
        if (expr is AstCallExpression call
            && (call.MethodName.Contains("get_Current") || call.MethodName == "Current")
            && call.Target is AstIdentifier callTarget
            && callTarget.Name == enumeratorVar)
            return true;

        // Pattern: enumerator.Current (as property/member access)
        if (expr is AstMemberAccess ma
            && ma.MemberName == "Current"
            && ma.Target is AstIdentifier maTarget
            && maTarget.Name == enumeratorVar)
            return true;

        return false;
    }

    /// <summary>
    /// Look for a Dispose() call after the while in the parent block.
    /// Returns the index if found, -1 otherwise.
    /// </summary>
    private static int TryFindDispose(AstBlock parentBlock, int whileIndex)
    {
        int searchEnd = Math.Min(parentBlock.Statements.Count, whileIndex + 3);
        for (int i = whileIndex + 1; i < searchEnd; i++)
        {
            if (IsDisposeCall(parentBlock.Statements[i]))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Check if a statement is a Dispose() call.
    /// </summary>
    private static bool IsDisposeCall(AstNode stmt)
    {
        if (stmt is AstExpressionStatement es)
        {
            if (es.Expression is AstCallExpression call && call.MethodName.Contains("Dispose"))
                return true;
            if (es.Expression is AstAssignment assign
                && assign.Value is AstCallExpression assignCall
                && assignCall.MethodName.Contains("Dispose"))
                return true;
        }
        return false;
    }

    private static bool IsIntZero(object? value) =>
        value is 0 or 0L or 0u or (ulong)0;

    private static bool IsIntOne(object? value) =>
        value is 1 or 1L or 1u or (ulong)1;

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static AstNode BuildUpdateStatement(string counterVar, string op, AstExpression step)
    {
        return new AstExpressionStatement
        {
            Expression = new AstAssignment
            {
                Target = new AstIdentifier { Name = counterVar },
                Value = new AstBinaryExpression
                {
                    Left = new AstIdentifier { Name = counterVar },
                    Operator = op,
                    Right = step
                }
            }
        };
    }

    private static bool IsComparisonOp(string op) =>
        op is ">=" or ">" or "<=" or "<" or "==" or "!=";

    private static bool IsArithmeticOp(string op) =>
        op is "+" or "-";

    private static string NegateComparisonOp(string op) => op switch
    {
        ">=" => "<",
        ">" => "<=",
        "<=" => ">",
        "<" => ">=",
        "==" => "!=",
        "!=" => "==",
        _ => op
    };

    private static bool IsNumeric(object? value) =>
        value is int or uint or long or ulong or short or ushort or byte or sbyte or float or double;
}
