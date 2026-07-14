using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Pipeline;
using Rosetta.Config;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Analysis.AST.Core;
using Rosetta.Analysis.AST.Utils;
using Rosetta.Analysis.Utils;

namespace Rosetta.Analysis.AST.Passes;

/// <summary>
/// Post-processing pass that removes IL2CPP runtime boilerplate from the AST.
/// Now implemented as an AstRewriter that performs sliding-window sequence matching.
/// </summary>
public sealed class BoilerplatePruner : AstRewriter
{
    private readonly HashSet<string> _nonNullVars;
    private readonly HashSet<string> _globalUsedVars;

    public int RemovedCount { get; private set; }

    private BoilerplatePruner(HashSet<string> nonNullVars, HashSet<string> globalUsedVars)
    {
        _nonNullVars = nonNullVars;
        _globalUsedVars = globalUsedVars;
    }

    /// <summary>Prune all boilerplate from a method's AST body.</summary>
    public static void Prune(AstMethod method)
    {
        if (Il2cppConfig.DisableBoilerplatePruner)
            return;

        var nonNullVars = new HashSet<string>();
        CollectNonNullVars(method.Body, nonNullVars);

        var globalUsedVars = new HashSet<string>();
        AstUtils.CollectIdentifierNames(method.Body, globalUsedVars);

        var pruner = new BoilerplatePruner(nonNullVars, globalUsedVars);
        var newBody = (AstBlock)pruner.VisitBlock(method.Body);
        
        int removedCount;
        do
        {
            var updatedUsedVars = new HashSet<string>();
            AstUtils.CollectIdentifierNames(newBody, updatedUsedVars);
            removedCount = CleanDeadPhisRecursively(newBody, updatedUsedVars);
        } while (removedCount > 0);

        method.Body = newBody;
        if (ConsoleReporter.IsTracing && pruner.RemovedCount > 0)
        {
            ConsoleReporter.Trace($"  BoilerplatePruner: removed {pruner.RemovedCount} boilerplate nodes from {method.MethodName}");
        }
    }

    private static int CleanDeadPhisRecursively(AstBlock block, HashSet<string> usedVars)
    {
        int count = BoilerplateMatchers.EliminateDeadPhiClusters(block.Statements, usedVars);
        foreach (var stmt in block.Statements.ToArray())
        {
            if (stmt is AstIfStatement ifs)
            {
                count += CleanDeadPhisRecursively(ifs.ThenBody, usedVars);
                if (ifs.ElseBody != null) count += CleanDeadPhisRecursively(ifs.ElseBody, usedVars);
            }
            else if (stmt is AstWhileStatement wh) count += CleanDeadPhisRecursively(wh.Body, usedVars);
            else if (stmt is AstForStatement fs) count += CleanDeadPhisRecursively(fs.Body, usedVars);
            else if (stmt is AstForeachStatement fe) count += CleanDeadPhisRecursively(fe.Body, usedVars);
        }
        return count;
    }

    private static void CollectNonNullVars(AstBlock block, HashSet<string> result)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is AstVariableDeclaration vd &&
                (vd.Initializer is AstNewExpression ||
                 (vd.Initializer is AstCallExpression call && call.MethodName.IsNonNullStringMethod())))
            {
                result.Add(vd.VarName);
            }
            else if (stmt is AstExpressionStatement es && es.Expression is AstAssignment assign &&
                     assign.Target is AstIdentifier targetId &&
                     (assign.Value is AstNewExpression ||
                      (assign.Value is AstCallExpression call2 && call2.MethodName.IsNonNullStringMethod())))
            {
                result.Add(targetId.Name);
            }

            if (stmt is AstIfStatement ifs)
            {
                CollectNonNullVars(ifs.ThenBody, result);
                if (ifs.ElseBody != null) CollectNonNullVars(ifs.ElseBody, result);
            }
            else if (stmt is AstWhileStatement wh) CollectNonNullVars(wh.Body, result);
            else if (stmt is AstForStatement fs) CollectNonNullVars(fs.Body, result);
            else if (stmt is AstForeachStatement fe) CollectNonNullVars(fe.Body, result);
        }
    }

    public override AstNode VisitBlock(AstBlock node)
    {
        bool changed = false;
        var newStmts = new List<AstNode>(node.Statements);

        // Apply block-level sequence matching TOP-DOWN (before children)
        if (PruneBlockSequences(newStmts))
        {
            changed = true;
        }

        // Now recursively process children on the SURVIVING statements
        var finalStmts = new List<AstNode>(newStmts.Count);
        foreach (var stmt in newStmts)
        {
            var rewritten = stmt.Accept(this);
            if (rewritten != stmt) changed = true;
            if (rewritten != null) finalStmts.Add(rewritten);
        }

        // Apply post-recursion cleanup passes
        if (ApplyPostCleanupPasses(finalStmts))
        {
            changed = true;
        }

        if (changed)
        {
            var res = new AstBlock();
            res.Statements.AddRange(finalStmts);
            return res;
        }

        return node;
    }

    private bool PruneBlockSequences(List<AstNode> stmts)
    {
        bool changed = false;
        for (int i = 0; i < stmts.Count; i++)
        {
            var stmt = stmts[i];

            if (stmt is AstIfStatement ifs)
            {
                if (TryPruneMetadataInit(ifs, stmts, ref i) ||
                    TryPruneEmptyClassInit(ifs, stmts, ref i) ||
                    TryPruneNullCheckOnAllocation(ifs, stmts, ref i) ||
                    TryPruneClassInitGuard(ifs, stmts, ref i) ||
                    TryPruneMethodRef(ifs, stmts, ref i) ||
                    TryPruneEmptyIf(ifs, stmts, ref i))
                {
                    changed = true;
                    continue;
                }
            }
        }
        return changed;
    }

    private bool TryPruneMetadataInit(AstIfStatement ifs, List<AstNode> stmts, ref int i)
    {
        if (BoilerplateMatchers.IsMetadataInitBlock(ifs, stmts, i, out int mdGuardIdx))
        {
            if (mdGuardIdx >= 0)
            {
                stmts.RemoveAt(mdGuardIdx);
                i--;
                RemovedCount++;
            }
            stmts.RemoveAt(i);
            i--;
            RemovedCount++;
            return true;
        }
        return false;
    }

    private bool TryPruneEmptyClassInit(AstIfStatement ifs, List<AstNode> stmts, ref int i)
    {
        if (BoilerplateMatchers.IsEmptyClassInitCheck(ifs))
        {
            stmts.RemoveAt(i);
            i--;
            RemovedCount++;
            return true;
        }
        return false;
    }

    private bool TryPruneNullCheckOnAllocation(AstIfStatement ifs, List<AstNode> stmts, ref int i)
    {
        if (BoilerplateMatchers.IsNullCheckOnAllocation(ifs, stmts, i, _nonNullVars))
        {
            stmts.RemoveAt(i);
            stmts.InsertRange(i, ifs.ThenBody.Statements);
            i--;
            RemovedCount++;
            return true;
        }
        return false;
    }

    private bool TryPruneClassInitGuard(AstIfStatement ifs, List<AstNode> stmts, ref int i)
    {
        if (BoilerplateMatchers.IsClassInitGuard(ifs, stmts, i, out int guardStmtIdx))
        {
            if (guardStmtIdx >= 0 && guardStmtIdx < i)
            {
                stmts.RemoveAt(guardStmtIdx);
                i--;
                RemovedCount++;
            }
            stmts.RemoveAt(i);
            stmts.InsertRange(i, ifs.ThenBody.Statements);
            i--;
            RemovedCount++;
            return true;
        }
        return false;
    }

    private bool TryPruneMethodRef(AstIfStatement ifs, List<AstNode> stmts, ref int i)
    {
        if (BoilerplateMatchers.IsMethodRefCondition(ifs))
        {
            stmts.RemoveAt(i);
            if (ifs.ThenBody.Statements.Count > 0)
            {
                stmts.InsertRange(i, ifs.ThenBody.Statements);
            }
            i--;
            RemovedCount++;
            return true;
        }
        return false;
    }

    private bool TryPruneEmptyIf(AstIfStatement ifs, List<AstNode> stmts, ref int i)
    {
        // if (ifs.ThenBody.Statements.Count == 0 && (ifs.ElseBody == null || ifs.ElseBody.Statements.Count == 0))
        // {
        //     stmts.RemoveAt(i);
        //     i--;
        //     RemovedCount++;
        //     return true;
        // }
        return false;
    }

    private bool ApplyPostCleanupPasses(List<AstNode> stmts)
    {
        bool changed = false;

        int taggedRemoved = BoilerplateMatchers.RemoveTaggedBoilerplate(stmts);
        if (taggedRemoved > 0)
        {
            RemovedCount += taggedRemoved;
            changed = true;
        }



        int addRemoved = BoilerplateMatchers.CollapseInlinedAddPatterns(stmts);
        if (addRemoved > 0)
        {
            RemovedCount += addRemoved;
            changed = true;
        }

        // Trailing void return
        if (stmts.Count > 0 && stmts[^1] is AstReturnStatement ret && ret.Value == null)
        {
            stmts.RemoveAt(stmts.Count - 1);
            RemovedCount++;
            changed = true;
        }

        return changed;
    }
}
