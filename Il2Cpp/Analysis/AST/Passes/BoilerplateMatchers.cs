using System;
using System.Collections.Generic;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Analysis.AST.Utils;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST.Passes;

public static class BoilerplateMatchers
{
    // ─── Pattern Matchers ───────────────────────────────────────────────────

    private static int FindPrecedingStatementIndex(IList<AstNode> statements, int ifIndex, Func<AstNode, bool> predicate)
    {
        for (int j = ifIndex - 1; j >= Math.Max(0, ifIndex - 2); j--)
        {
            if (predicate(statements[j]))
                return j;
        }
        return -1;
    }

    public static bool IsMetadataInitBlock(AstIfStatement ifs, IList<AstNode> statements, int ifIndex, out int guardStmtIdx)
    {
        guardStmtIdx = -1;

        bool isMetadataCond = IsMetadataVarCondition(ifs.Condition);

        if (!isMetadataCond)
        {
            string? varName = GetSimpleConditionVarName(ifs.Condition);
            if (varName != null)
            {
                guardStmtIdx = FindPrecedingStatementIndex(statements, ifIndex, node =>
                {
                    if (node is AstExpressionStatement es && es.Tag == StatementTag.MetadataVar)
                        return true;
                    if (node is AstVariableDeclaration vd && vd.VarName == varName && IsMetadataVarExpr(vd.Initializer))
                        return true;
                    return false;
                });
                if (guardStmtIdx >= 0)
                    isMetadataCond = true;
            }
        }

        if (!isMetadataCond)
            return false;

        if (ifs.ThenBody.Statements.Count == 0)
            return true;

        foreach (var stmt in ifs.ThenBody.Statements)
        {
            if (stmt is AstExpressionStatement bodyEs && bodyEs.Tag == StatementTag.MetadataPageStore)
                return true;
            if (stmt is AstExpressionStatement bodyEs2 && ContainsMetadataPageRef(bodyEs2.Expression))
                return true;
        }
        return false;
    }

    public static bool IsEmptyClassInitCheck(AstIfStatement ifs)
    {
        if (ifs.ThenBody.Statements.Count != 0) return false;
        return IsClassInitCondition(ifs.Condition);
    }

    public static bool IsClassInitGuard(AstIfStatement ifs, IList<AstNode> statements, int ifIndex, out int guardStmtIdx)
    {
        guardStmtIdx = -1;

        if (ifs.ThenBody.Statements.Count == 0) return false;
        if (ifs.ElseBody != null && ifs.ElseBody.Statements.Count > 0) return false;

        if (IsClassInitCondition(ifs.Condition))
            return true;

        string? varName = GetSimpleConditionVarName(ifs.Condition);
        if (varName != null)
        {
            guardStmtIdx = FindPrecedingStatementIndex(statements, ifIndex, node =>
            {
                if (node is AstExpressionStatement es && (es.Tag is StatementTag.ClassInitFlag or StatementTag.VTableLoad))
                    return true;
                if (node is AstVariableDeclaration vd && vd.VarName == varName && IsClassInitExpr(vd.Initializer))
                    return true;
                return false;
            });
            if (guardStmtIdx >= 0)
                return true;
        }

        return false;
    }

    public static bool IsNullCheckOnAllocation(AstIfStatement ifs, IList<AstNode> statements, int ifIndex, HashSet<string> nonNullVars)
    {
        if (ifs.ElseBody != null && ifs.ElseBody.Statements.Count > 0)
            return false;

        string? condVar = GetSimpleConditionVarName(ifs.Condition);
        if (condVar == null) return false;

        bool isNullCheck = false;
        if (nonNullVars.Contains(condVar))
        {
            isNullCheck = true;
        }
        else
        {
            for (int j = ifIndex - 1; j >= 0; j--)
            {
                if (statements[j] is AstVariableDeclaration vd && vd.VarName == condVar && vd.Initializer is AstNewExpression)
                    { isNullCheck = true; break; }
                
                if (statements[j] is AstExpressionStatement es && es.Expression is AstIdentifier id && IsNewArrayAssignment(id.Name, condVar))
                    { isNullCheck = true; break; }
            }
        }

        if (!isNullCheck) return false;

        // Safety: do not prune if the if-body assigns to a variable that is also
        // assigned in the surrounding scope before the if. This pattern arises from
        // SSA phi nodes where one source is in the branch header block (common in ARM32).
        // Inlining the body would unconditionally overwrite the default phi value.
        if (BodyOverwritesOuterVariable(ifs.ThenBody, statements, ifIndex))
            return false;

        return true;
    }

    /// <summary>
    /// Checks whether any variable assigned inside the if-body is also assigned
    /// in the surrounding scope before the if statement. This detects phi-split
    /// patterns where flattening the if would destroy conditional semantics.
    /// </summary>
    public static bool BodyOverwritesOuterVariable(AstBlock body, IList<AstNode> statements, int ifIndex)
    {
        var bodyAssignedVars = new HashSet<string>();
        foreach (var stmt in body.Statements)
        {
            if (stmt is AstExpressionStatement es && es.Expression is AstAssignment assign &&
                assign.Target is AstIdentifier targetId)
                bodyAssignedVars.Add(targetId.Name);
            else if (stmt is AstVariableDeclaration vd)
                bodyAssignedVars.Add(vd.VarName);
        }

        if (bodyAssignedVars.Count == 0) return false;

        for (int j = 0; j < ifIndex; j++)
        {
            if (statements[j] is AstExpressionStatement es && es.Expression is AstAssignment assign &&
                assign.Target is AstIdentifier targetId && bodyAssignedVars.Contains(targetId.Name))
                return true;
            if (statements[j] is AstVariableDeclaration vd && bodyAssignedVars.Contains(vd.VarName))
                return true;
        }

        return false;
    }


    public static bool IsMethodRefCondition(AstIfStatement ifs)
    {
        return ContainsMethodRef(ifs.Condition);
    }

    // ─── Condition Inspection Helpers ────────────────────────────────────────

    private static bool IsMetadataVarCondition(AstExpression cond)
    {
        if (cond is AstIdentifier id && id.Name == "metadata_var") return true;
        if (cond is AstUnaryExpression { Operator: "!" } un) return IsMetadataVarCondition(un.Operand);
        if (cond is AstBinaryExpression bin && (bin.Operator == "&" || bin.Operator == "|" || bin.Operator == "^" || bin.Operator == "==" || bin.Operator == "!="))
            return IsMetadataVarCondition(bin.Left) || IsMetadataVarCondition(bin.Right);
        if (ContainsMetadataPageRef(cond)) return true;
        return false;
    }

    private static bool IsMetadataVarExpr(AstExpression? expr)
    {
        if (expr is AstIdentifier id && id.Name == "metadata_var") return true;
        return false;
    }

    private static bool ContainsMetadataPageRef(AstExpression expr)
    {
        if (expr is AstIdentifier id && (id.Name.Contains("il2cpp_metadata_page") || IsMetadataPageHexAddress(id.Name))) return true;
        if (expr is AstLiteral lit && lit.Value is string sv && IsMetadataPageHexAddress(sv)) return true;
        if (expr is AstAssignment assign) return ContainsMetadataPageRef(assign.Target) || ContainsMetadataPageRef(assign.Value);
        if (expr is AstBinaryExpression bin) return ContainsMetadataPageRef(bin.Left) || ContainsMetadataPageRef(bin.Right);
        if (expr is AstMemberAccess ma) return ma.MemberName.Contains("il2cpp_metadata_page") || ContainsMetadataPageRef(ma.Target);
        if (expr is AstMemoryAccess mem) return ContainsMetadataPageRef(mem.Base);
        if (expr is AstIndexAccess idx) return ContainsMetadataPageRef(idx.Target);
        return false;
    }

    private static bool IsMetadataPageHexAddress(string name)
    {
        if (name.StartsWith("0x") && name.Length >= 8)
        {
            for (int i = 2; i < name.Length; i++)
            {
                char c = name[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }
        return false;
    }

    private static bool ContainsMethodRef(AstExpression expr)
    {
        if (expr is AstIdentifier id && id.Name.Contains("MethodRef(")) return true;
        if (expr is AstCallExpression call && call.MethodName.Contains("MethodRef")) return true;
        if (expr is AstBinaryExpression bin) return ContainsMethodRef(bin.Left) || ContainsMethodRef(bin.Right);
        if (expr is AstUnaryExpression un) return ContainsMethodRef(un.Operand);
        return false;
    }

    private static bool IsClassInitCondition(AstExpression cond)
    {
        if (cond is AstUnaryExpression { Operator: "!" } un) return IsClassInitCondition(un.Operand);
        if (cond is AstMemberAccess ma && (ma.MemberName is "initialized" or "cctor_finished")) return true;
        if (cond is AstBinaryExpression bin && (IsClassInitExpr(bin.Left) || IsClassInitExpr(bin.Right))) return true;
        if (cond is AstIdentifier id) return IsClassInitIdentifierValue(id.Name);
        return IsClassInitExpr(cond);
    }

    private static bool IsClassInitExpr(AstExpression? expr)
    {
        if (expr == null) return false;
        if (expr is AstMemberAccess ma && ma.MemberName is "initialized" or "cctor_finished") return true;
        if (expr is AstIndexAccess ia && ia.Target is AstIdentifier iat && iat.Name.StartsWith("vtable")) return true;
        if (expr is AstCallExpression call && call.MethodName == "class_init") return true;
        if (expr is AstMemoryAccess mem && mem.Offset == Rosetta.Common.Constants.ClassInitFlagOffset) return true;
        if (expr is AstIdentifier id) return IsClassInitIdentifierValue(id.Name);
        return false;
    }

    private static bool IsClassInitIdentifierValue(string name)
    {
        if (name.StartsWith("class_init")) return true;
        if (name.Length >= 7 && name[..7] == "vtable[") return true;
        if (name.StartsWith("vtable<")) return true;
        return false;
    }

    private static string? GetSimpleConditionVarName(AstExpression cond)
    {
        if (cond is AstIdentifier id && !string.IsNullOrEmpty(id.Name) && !id.Name.Contains(' ') && !id.Name.Contains('(')) return id.Name;
        if (cond is AstUnaryExpression { Operator: "!" } un && un.Operand is AstIdentifier unId) return unId.Name;
        return null;
    }

    // ─── Tagged Boilerplate Removal ─────────────────────────────────────────

    public static int RemoveTaggedBoilerplate(IList<AstNode> statements)
    {
        var deadToRemove = new List<AstNode>();

        foreach (var node in statements)
        {
            if (node is AstExpressionStatement es)
            {
                if (es.Tag == StatementTag.TypeOf && !IsUsedAsArgument(es.Expression))
                {
                    deadToRemove.Add(node);
                    continue;
                }
                if (es.Tag is StatementTag.VTableLoad or StatementTag.MethodRef or StatementTag.MetadataPageStore or StatementTag.ClassInitFlag)
                {
                    deadToRemove.Add(node);
                    continue;
                }
                if (es.Expression is AstCallExpression call && call.MethodName.Contains("MethodRef"))
                {
                    deadToRemove.Add(node);
                    continue;
                }
                if (ContainsMetadataPageRef(es.Expression))
                {
                    deadToRemove.Add(node);
                    continue;
                }
                if (IsInlinedCollectionBoilerplate(es.Expression))
                {
                    deadToRemove.Add(node);
                    continue;
                }
                if (es.Expression is AstIdentifier mid && mid.Name.Contains("MethodRef("))
                {
                    deadToRemove.Add(node);
                    continue;
                }
            }

            if (node is AstVariableDeclaration vd)
            {
                if (vd.Initializer != null)
                {
                    if (IsInlinedCollectionBoilerplate(vd.Initializer) || ContainsMethodRef(vd.Initializer))
                    {
                        deadToRemove.Add(node);
                        continue;
                    }
                    if (vd.Initializer is AstCallExpression typeofCall && typeofCall.MethodName == "typeof")
                    {
                        deadToRemove.Add(node);
                        continue;
                    }
                }
            }
        }

        foreach (var node in deadToRemove)
            statements.Remove(node);

        return deadToRemove.Count;
    }

    // ─── Inlined Collection Method Detection ────────────────────────────────

    private static bool IsInlinedCollectionBoilerplate(AstExpression expr)
    {
        if (expr is AstAssignment assign)
        {
            if (assign.Target is AstMemberAccess ma && ma.MemberName == "_version") return true;
            if (assign.Target is AstMemberAccess ma2 && ma2.MemberName == "_items") return true;
        }

        if (expr is AstIdentifier id)
        {
            string name = id.Name;
            if (name.Length >= 10 && name[..8] == ".Length =") return true;
            if (name.Contains("<< 3)") && name.Contains("[0] = ")) return true;
            if (name.Contains("<< 50)") && name.Contains("[0] = ")) return true;
        }
        return false;
    }

    public static int CollapseInlinedAddPatterns(IList<AstNode> statements)
    {
        int removed = 0;

        for (int i = 0; i < statements.Count; i++)
        {
            var node = statements[i];

            if (node is AstIfStatement ifs)
            {
                var (foundTarget, foundValue) = FindAddWithResize(ifs.ThenBody);
                if (foundTarget == null || foundValue == null) continue;

                statements[i] = new AstExpressionStatement
                {
                    Expression = new AstCallExpression
                    {
                        MethodName = "Add",
                        Target = foundTarget,
                        Arguments = new List<AstExpression> { foundValue }
                    }
                };
                removed++;

                for (int j = i - 1; j >= Math.Max(0, i - 5); j--)
                {
                    bool isBoilerplate = false;
                    if (statements[j] is AstExpressionStatement prevEs)
                    {
                        bool isLengthOfTarget = prevEs.Expression is AstMemberAccess ma && ma.MemberName == "Length" && IsSameTarget(ma.Target, foundTarget);
                        isBoilerplate = isLengthOfTarget || prevEs.Tag == StatementTag.MethodRef ||
                                       IsInlinedCollectionBoilerplate(prevEs.Expression) ||
                                       ContainsMethodRef(prevEs.Expression);
                    }
                    else if (statements[j] is AstVariableDeclaration vd && vd.Initializer != null)
                    {
                        bool isLengthOfTarget = vd.Initializer is AstMemberAccess ma && ma.MemberName == "Length" && IsSameTarget(ma.Target, foundTarget);
                        isBoilerplate = isLengthOfTarget || IsInlinedCollectionBoilerplate(vd.Initializer) ||
                                       ContainsMethodRef(vd.Initializer);
                    }

                    if (isBoilerplate)
                    {
                        statements.RemoveAt(j);
                        i--;
                        removed++;
                    }
                }
            }
        }
        return removed;
    }

    private static bool IsSameTarget(AstExpression a, AstExpression b)
    {
        if (a is AstIdentifier idA && b is AstIdentifier idB) return idA.Name == idB.Name;
        return false;
    }

    private static (AstExpression? target, AstExpression? value) FindAddWithResize(AstBlock block)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is AstExpressionStatement es)
            {
                if (es.Expression is AstCallExpression call && call.MethodName == "AddWithResize")
                {
                    var value = call.Arguments.Count > 0 ? call.Arguments[0] : new AstIdentifier { Name = "?" };
                    return (call.Target ?? new AstIdentifier { Name = "obj" }, value);
                }
                if (es.Expression is AstAssignment assign && assign.Value is AstCallExpression assignCall && assignCall.MethodName == "AddWithResize")
                {
                    var value = assignCall.Arguments.Count > 0 ? assignCall.Arguments[0] : new AstIdentifier { Name = "?" };
                    return (assignCall.Target ?? new AstIdentifier { Name = "obj" }, value);
                }
                if (es.Expression is AstIdentifier id)
                {
                    int addIdx = id.Name.IndexOf(".AddWithResize(", StringComparison.Ordinal);
                    if (addIdx >= 0)
                    {
                        string target = id.Name[..addIdx];
                        int argStart = addIdx + ".AddWithResize(".Length;
                        int argEnd = id.Name.IndexOf(')', argStart);
                        string valueStr = argEnd > argStart ? id.Name[argStart..argEnd] : "?";
                        return (new AstIdentifier { Name = target }, new AstIdentifier { Name = valueStr });
                    }
                }
            }

            if (stmt is AstIfStatement ifs)
            {
                var result = FindAddWithResize(ifs.ThenBody);
                if (result.target != null) return result;
                if (ifs.ElseBody != null)
                {
                    result = FindAddWithResize(ifs.ElseBody);
                    if (result.target != null) return result;
                }
            }
        }
        return (null, null);
    }

    // ─── Dead Phi Cluster Elimination ───────────────────────────────────────

    public static int EliminateDeadPhiClusters(IList<AstNode> statements, HashSet<string> usedVars)
    {
        var forwardDecls = new List<(int index, string varName)>();
        var tempAssigns = new List<(int index, string varName)>();
        int rewrittenCount = 0;

        for (int i = 0; i < statements.Count; i++)
        {
            var node = statements[i];

            if (node is AstVariableDeclaration vd)
            {
                if (vd.Initializer == null)
                {
                    forwardDecls.Add((i, vd.VarName));
                }
                else if (IsSsaTempName(vd.VarName))
                {
                    if (AstUtils.IsSideEffectFree(vd.Initializer))
                    {
                        tempAssigns.Add((i, vd.VarName));
                    }
                    else if (!usedVars.Contains(vd.VarName) && vd.Initializer is AstCallExpression)
                    {
                        statements[i] = new AstExpressionStatement { Expression = vd.Initializer };
                        rewrittenCount++;
                    }
                }
                continue;
            }

            if (node is AstExpressionStatement es && es.Expression is AstAssignment assign)
            {
                string? targetName = AstUtils.GetIdentifierName(assign.Target);
                if (targetName != null && IsSsaTempName(targetName))
                {
                    if (AstUtils.IsSideEffectFree(assign.Value))
                    {
                        tempAssigns.Add((i, targetName));
                    }
                    else if (!usedVars.Contains(targetName) && assign.Value is AstCallExpression)
                    {
                        statements[i] = new AstExpressionStatement { Expression = assign.Value };
                        rewrittenCount++;
                    }
                    continue;
                }
            }
        }

        var indicesToRemove = new List<int>();
        foreach (var (idx, name) in forwardDecls)
        {
            if (!usedVars.Contains(name)) indicesToRemove.Add(idx);
        }
        foreach (var (idx, name) in tempAssigns)
        {
            if (!usedVars.Contains(name)) indicesToRemove.Add(idx);
        }

        indicesToRemove.Sort();
        indicesToRemove.Reverse();
        foreach (int idx in indicesToRemove)
        {
            if (idx < statements.Count)
                statements.RemoveAt(idx);
        }

        return indicesToRemove.Count + rewrittenCount;
    }

    private static bool IsUsedAsArgument(AstExpression expr)
    {
        if (expr is AstCallExpression call)
        {
            if (call.MethodName.Contains("Debug.Log") || call.MethodName == "GetType") return true;
        }
        if (expr is AstAssignment assign)
        {
            if (assign.Value is AstCallExpression valCall && valCall.MethodName == "typeof")
            {
                string? targetName = AstUtils.GetIdentifierName(assign.Target);
                if (targetName != null && !IsSsaTempName(targetName)) return true;
            }
        }
        return false;
    }

    private static bool IsSsaTempName(string name)
    {
        if (name.Length < 3) return false;
        char prefix = name[0];
        if (prefix is not ('x' or 'w' or 's' or 'd' or 'q' or 'v')) return false;
        int i = 1;
        if (!char.IsDigit(name[i])) return false;
        while (i < name.Length && char.IsDigit(name[i])) i++;
        if (i >= name.Length || name[i] != '_') return false;
        i++;
        if (i >= name.Length || !char.IsDigit(name[i])) return false;
        return true;
    }

    private static bool IsNewArrayAssignment(string text, string varName)
    {
        int eqIdx = text.IndexOf(" = ");
        if (eqIdx < 0) return false;
        string lhs = text[..eqIdx];
        if (!lhs.Contains(varName)) return false;
        string rhs = text[(eqIdx + 3)..];
        if (rhs.Length >= 5 && rhs[..4] == "new " && rhs.Contains('[')) return true;
        return false;
    }
}
