using System;
using System.Linq;
using System.Collections.Generic;
using Rosetta.Pipeline;
using Rosetta.Analysis.Utils;
using Rosetta.Analysis.AST.Utils;

namespace Rosetta.Analysis.AST.Passes;

/// <summary>
/// Post-processing pass that simplifies expressions in the AST.
///
/// Operates PURELY on structural AST nodes. Zero string matching or Regex.
///
/// Simplifications:
///   1. Negated comparison folding: !(x <= 2) → x > 2
///   2. Double negation: !!x → x
///   3. Length-as-bool guards: if (x.Length) → unwraps to true
///   4. Ternary folding: if/else assigning to same target → ternary expression
///   5. Recursive Variable Inlining: Propagates single-use temps deeply into nested blocks.
/// </summary>
public static class ExprSimplifier
{
    public static void Simplify(AstMethod method)
    {
        int simplified = SimplifyBlock(method.Body, new List<AstExpression>());

        var paramNames = new HashSet<string>();
        foreach (var p in method.Parameters)
        {
            var parts = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                paramNames.Add(parts[^1]);
        }
        paramNames.UnionWith(method.OutVariableDeclarations);

        int inlined = InlineVariables(method.Body, paramNames);

        // Iterative second pass: each SimplifyBlock may inline if-bodies that reveal
        // new always-true conditions (chained IL2CPP null-guards). Loop until stable.
        if (inlined > 0)
        {
            int pass;
            while ((pass = SimplifyBlock(method.Body, new List<AstExpression>())) > 0)
                simplified += pass;
        }

        if (simplified > 0 || inlined > 0)
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"  ExprSimplifier: {simplified} simplifications, {inlined} inlines in {method.MethodName}");
    }

    private static int SimplifyBlock(AstBlock block, List<AstExpression> parentKnownTrue)
    {
        int count = 0;
        var knownTrue = new List<AstExpression>(parentKnownTrue);

        for (int i = 0; i < block.Statements.Count; i++)
        {
            var node = block.Statements[i];

            // Invalidate any known true expressions that are mutated/reassigned in this statement.
            var walker = new MutationCollector();
            node.Accept(walker);
            foreach (var mutated in walker.MutatedExpressions)
            {
                knownTrue.RemoveAll(c => ContainsExpression(c, mutated));
            }

            if (node is AstIfStatement ifs)
            {
                if (TrySimplifyIfStatement(block, ref i, ifs, knownTrue, ref count))
                {
                    continue;
                }
            }
            else if (node is AstWhileStatement wh)
            {
                var newCond = SimplifyExpr(wh.Condition);
                if (newCond != wh.Condition)
                {
                    wh = new AstWhileStatement { Condition = newCond, Body = wh.Body };
                    block.Statements[i] = wh;
                    count++;
                }
                count += SimplifyBlock(wh.Body, knownTrue);
            }
            else if (node is AstForStatement fs)
            {
                if (fs.Init != null) fs.Init = SimplifyStmt(fs.Init, ref count);
                if (fs.Condition != null) fs.Condition = SimplifyExpr(fs.Condition);
                if (fs.Update != null) fs.Update = SimplifyStmt(fs.Update, ref count);
                count += SimplifyBlock(fs.Body, knownTrue);
            }
            else if (node is AstForeachStatement fe)
            {
                count += SimplifyBlock(fe.Body, knownTrue);
            }
            else if (TryFoldReturnPeephole(block, i, ref count))
            {
                i--;
                continue;
            }
            else if (node is AstVariableDeclaration vd)
            {
                TrySimplifyVariableDeclaration(vd, ref count);
            }
            else if (node is AstExpressionStatement es && es.Expression is AstAssignment)
            {
                TrySimplifyAssignmentStatement(block, i, es, ref count);
            }
            else
            {
                var newStmt = SimplifyStmt(node, ref count);
                if (newStmt != node)
                {
                    block.Statements[i] = newStmt;
                }
            }
        }

        return count;
    }

    private static bool TrySimplifyIfStatement(AstBlock block, ref int i, AstIfStatement ifs, List<AstExpression> knownTrue, ref int count)
    {
        if (TryFoldTernary(ifs, block, i, out var foldedNode, out int declarationToRemoveIndex))
        {
            if (declarationToRemoveIndex >= 0)
            {
                block.Statements.RemoveAt(declarationToRemoveIndex);
                i--;
            }
            block.Statements[i] = foldedNode;
            count++;
            return true;
        }

        var newCond = SimplifyExpr(ifs.Condition);
        if (newCond != ifs.Condition)
        {
            ifs = new AstIfStatement { Condition = newCond, ThenBody = ifs.ThenBody, ElseBody = ifs.ElseBody };
            block.Statements[i] = ifs;
            count++;
        }

        if (IsKnownTrueCondition(ifs.Condition, knownTrue) || IsAlwaysTrueCondition(ifs, block, i))
        {
            // Safety: do not fold if the body assigns to a variable also assigned before
            // the if — this is a phi-split pattern where flattening loses conditionality.
            if (!BoilerplateMatchers.BodyOverwritesOuterVariable(ifs.ThenBody, block.Statements, i))
            {
                block.Statements.RemoveAt(i);
                count += SimplifyBlock(ifs.ThenBody, knownTrue);
                block.Statements.InsertRange(i, ifs.ThenBody.Statements);
                count++;
                i--;
                return true;
            }
        }

        var thenKnownTrue = new List<AstExpression>(knownTrue);
        var elseKnownTrue = new List<AstExpression>(knownTrue);

        var thenGuard = GetNullGuardVariable(ifs.Condition, "!=") ?? NormalizeGuard(ifs.Condition);
        if (IsGuardCandidate(thenGuard))
        {
            thenKnownTrue.Add(thenGuard);
        }

        var elseGuard = GetNullGuardVariable(ifs.Condition, "==");
        if (elseGuard != null && IsGuardCandidate(elseGuard))
        {
            elseKnownTrue.Add(elseGuard);
        }

        count += SimplifyBlock(ifs.ThenBody, thenKnownTrue);
        if (ifs.ElseBody != null)
            count += SimplifyBlock(ifs.ElseBody, elseKnownTrue);

        return false;
    }

    private static bool TryFoldReturnPeephole(AstBlock block, int i, ref int count)
    {
        var node = block.Statements[i];
        if (node is AstVariableDeclaration vd && vd.Initializer != null)
        {
            if (i + 1 < block.Statements.Count && block.Statements[i + 1] is AstReturnStatement ret && ret.Value is AstIdentifier retId && retId.Name == vd.VarName)
            {
                block.Statements[i + 1] = new AstReturnStatement { Value = vd.Initializer };
                block.Statements.RemoveAt(i);
                count++;
                return true;
            }
        }
        else if (node is AstExpressionStatement es && es.Expression is AstAssignment exprAssign && exprAssign.Target is AstIdentifier id)
        {
            if (i + 1 < block.Statements.Count && block.Statements[i + 1] is AstReturnStatement ret && ret.Value is AstIdentifier retId && retId.Name == id.Name)
            {
                block.Statements[i + 1] = new AstReturnStatement { Value = exprAssign.Value };
                block.Statements.RemoveAt(i);
                count++;
                return true;
            }
        }
        return false;
    }

    private static void TrySimplifyVariableDeclaration(AstVariableDeclaration vd, ref int count)
    {
        if (vd.Initializer != null)
        {
            var newInit = SimplifyExpr(vd.Initializer);
            if (newInit != vd.Initializer)
            {
                count++;
                vd.Initializer = newInit;
            }
        }
    }

    private static bool TrySimplifyAssignmentStatement(AstBlock block, int i, AstExpressionStatement es, ref int count)
    {
        var newExpr = SimplifyExpr(es.Expression);
        if (newExpr != es.Expression)
        {
            count++;
            block.Statements[i] = new AstExpressionStatement { Expression = newExpr };
            return true;
        }
        return false;
    }


    private static AstNode SimplifyStmt(AstNode stmt, ref int count)
    {
        if (stmt is AstExpressionStatement es)
        {
            var newExpr = SimplifyExpr(es.Expression);
            if (newExpr != es.Expression)
            {
                count++;
                return new AstExpressionStatement { Expression = newExpr };
            }
        }
        else if (stmt is AstVariableDeclaration vd && vd.Initializer != null)
        {
            var newInit = SimplifyExpr(vd.Initializer);
            if (newInit != vd.Initializer)
            {
                count++;
                vd.Initializer = newInit;
            }
        }
        else if (stmt is AstReturnStatement ret && ret.Value != null)
        {
            var newVal = SimplifyExpr(ret.Value);
            if (newVal != ret.Value)
            {
                count++;
                ret.Value = newVal;
            }
        }
        return stmt;
    }

    private static AstExpression SimplifyExpr(AstExpression expr)
    {
        if (expr == null) return null!;
        var visitor = new ExprSimplificationVisitor();
        return (AstExpression)(expr.Accept(visitor) ?? expr);
    }

    private static bool IsAlwaysTrueCondition(AstIfStatement ifs, AstBlock parentBlock, int ifIndex)
    {
        if (ifs.ElseBody != null && ifs.ElseBody.Statements.Count > 0) return false;

        var cond = NormalizeGuard(ifs.Condition);

        if (cond is AstMemberAccess ma)
        {
            if (ma.MemberName == "Length")
                return true;

            if ((ma.MemberName == "_items" || ma.MemberName == "items") && ma.Target is AstIdentifier targetId)
            {
                for (int j = ifIndex - 1; j >= 0; j--)
                {
                    AstExpression? initializer = null;
                    bool found = false;

                    if (parentBlock.Statements[j] is AstVariableDeclaration vd && vd.VarName == targetId.Name)
                    {
                        initializer = vd.Initializer;
                        found = true;
                    }
                    else if (parentBlock.Statements[j] is AstExpressionStatement es &&
                             es.Expression is AstAssignment assign &&
                             assign.Target is AstIdentifier assignId && assignId.Name == targetId.Name)
                    {
                        initializer = assign.Value;
                        found = true;
                    }

                    if (found)
                    {
                        if (initializer is AstNewExpression ne && ne.TypeName != null && Rosetta.Common.TypeUtils.IsStandardCollectionType(ne.TypeName))
                        {
                            return true;
                        }
                        break;
                    }
                }
            }
        }

        // Case 2: IL2CPP array bounds-check guards around known-size array stores.
        //   (arr.Length & mask) != 0, arr.Length != N, arr.Length > N
        // Excludes loop guards (arr.Length >= 1) which use >= operator.
        if (ifs.Condition is AstBinaryExpression cmp && IsArrayBoundsCheck(cmp))
            return true;

        if (cond is AstIdentifier condId)
        {
            // Search all preceding statements for where condId was assigned.
            // Stop if we find a reassignment to a non-deterministic source.
            for (int j = ifIndex - 1; j >= 0; j--)
            {
                if (parentBlock.Statements[j] is AstVariableDeclaration vd &&
                    vd.VarName == condId.Name)
                {
                    return IsNonNullInitializer(vd.Initializer);
                }
                if (parentBlock.Statements[j] is AstExpressionStatement es &&
                    es.Expression is AstAssignment assign &&
                    assign.Target is AstIdentifier targetId && targetId.Name == condId.Name)
                {
                    return IsNonNullInitializer(assign.Value);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Check if an expression always produces a non-null result.
    /// Used to identify IL2CPP null-guard patterns where if(var) is always true.
    /// </summary>
    private static bool IsNonNullInitializer(AstExpression? init)
    {
        if (init == null) return false;
        if (init is AstMemberAccess ma && ma.MemberName == "Length") return true;
        if (init is AstLiteral lit && lit.Value != null) return true;
        if (init is AstNewExpression) return true;
        if (init is AstCallExpression call && call.MethodName.IsNonNullStringMethod()) return true;
        // Note: AstCallExpression is NOT included in general — method calls can return null
        // (e.g., GetComponent<T>() returns null when component is missing).
        return false;
    }

    /// <summary>
    /// Matches IL2CPP .Length guard patterns (safe to prune).
    /// Any comparison with .Length on the LEFT in an else-less if is IL2CPP boilerplate:
    ///   (arr.Length &amp; mask) != 0, arr.Length != N, arr.Length &gt; N, arr.Length &gt;= 1
    /// .Length on the RIGHT (e.g., i &lt; arr.Length) is a loop condition — not matched here.
    /// </summary>
    private static bool IsArrayBoundsCheck(AstBinaryExpression cmp)
    {
        // Pattern A: (arr.Length & mask) OP literal
        if (cmp.Left is AstBinaryExpression inner && inner.Operator == "&" &&
            ContainsLengthMemberAccess(inner.Left) &&
            cmp.Right is AstLiteral)
            return true;

        // Pattern B: arr.Length OP literal — any operator
        if (cmp.Left is AstMemberAccess ma && ma.MemberName == "Length" &&
            cmp.Right is AstLiteral)
            return true;

        return false;
    }

    private static bool ContainsLengthMemberAccess(AstExpression expr)
    {
        if (expr is AstMemberAccess ma && ma.MemberName == "Length")
            return true;
        if (expr is AstBinaryExpression bin)
            return ContainsLengthMemberAccess(bin.Left);
        return false;
    }

    private static bool TryFoldTernary(AstIfStatement ifs, AstBlock block, int ifIndex, out AstNode foldedNode, out int declarationToRemoveIndex)
    {
        foldedNode = null!;
        declarationToRemoveIndex = -1;

        if (ifs.ThenBody.Statements.Count != 1 || ifs.ElseBody == null || ifs.ElseBody.Statements.Count != 1) 
            return false;

        var thenStmt = ifs.ThenBody.Statements[0] as AstExpressionStatement;
        var elseStmt = ifs.ElseBody.Statements[0] as AstExpressionStatement;

        if (thenStmt?.Expression is not AstAssignment thenAssign || 
            elseStmt?.Expression is not AstAssignment elseAssign)
            return false;

        if (thenAssign.Target is not AstIdentifier thenTarget || 
            elseAssign.Target is not AstIdentifier elseTarget || 
            thenTarget.Name != elseTarget.Name)
            return false;

        string targetVar = thenTarget.Name;

        AstExpression cond = ifs.Condition;
        AstExpression thenVal = thenAssign.Value;
        AstExpression elseVal = elseAssign.Value;

        if (cond is AstUnaryExpression { Operator: "!" } negCond)
        {
            cond = negCond.Operand;
            thenVal = elseAssign.Value;
            elseVal = thenAssign.Value;
        }

        AstExpression ternary = new AstBinaryExpression
        {
            Operator = "?",
            Left = cond,
            Right = new AstBinaryExpression
            {
                Operator = ":",
                Left = thenVal,
                Right = elseVal
            }
        };

        for (int j = ifIndex - 1; j >= 0 && j >= ifIndex - 2; j--)
        {
            if (block.Statements[j] is AstVariableDeclaration vd && vd.VarName == targetVar && vd.Initializer == null)
            {
                declarationToRemoveIndex = j;
                foldedNode = new AstVariableDeclaration
                {
                    TypeName = vd.TypeName,
                    VarName = targetVar,
                    Initializer = ternary
                };
                return true;
            }
        }

        foldedNode = new AstExpressionStatement
        {
            Expression = new AstAssignment
            {
                Target = new AstIdentifier { Name = targetVar },
                Value = ternary
            }
        };
        return true;
    }

    private static int InlineVariables(AstBlock block, HashSet<string> paramNames)
    {
        var assignmentCounts = new Dictionary<string, int>();
        CountAssignments(block, assignmentCounts);

        return InlineVariablesRecursive(block, paramNames, assignmentCounts);
    }



    private static int InlineVariablesRecursive(AstBlock block, HashSet<string> paramNames, Dictionary<string, int> assignmentCounts)
    {
        int count = 0;

        for (int i = 0; i < block.Statements.Count; i++)
        {
            var node = block.Statements[i];
            
            if (node is AstIfStatement ifs)
            {
                count += InlineVariablesRecursive(ifs.ThenBody, paramNames, assignmentCounts);
                if (ifs.ElseBody != null) count += InlineVariablesRecursive(ifs.ElseBody, paramNames, assignmentCounts);
            }
            else if (node is AstWhileStatement wh) count += InlineVariablesRecursive(wh.Body, paramNames, assignmentCounts);
            else if (node is AstForStatement fs) count += InlineVariablesRecursive(fs.Body, paramNames, assignmentCounts);
            else if (node is AstForeachStatement fe) count += InlineVariablesRecursive(fe.Body, paramNames, assignmentCounts);

            if (TryGetInlineCandidate(node, out string varName, out AstExpression value))
            {
                if (CanInlineCandidate(varName, value, block.Statements, i + 1, paramNames, assignmentCounts))
                {
                    bool anyReplaced = false;
                    for (int j = i + 1; j < block.Statements.Count; j++)
                    {
                        var newStmt = ReplaceInStmt(block.Statements[j], varName, value, ref anyReplaced);
                        if (newStmt != block.Statements[j])
                        {
                            block.Statements[j] = newStmt;
                        }
                    }

                    if (anyReplaced)
                    {
                        block.Statements.RemoveAt(i);
                        count++;
                        return count + InlineVariablesRecursive(block, paramNames, assignmentCounts);
                    }
                }
            }
        }

        return count;
    }

    private static bool TryGetInlineCandidate(AstNode node, out string varName, out AstExpression value)
    {
        varName = null!;
        value = null!;

        if (node is AstVariableDeclaration vd && vd.Initializer != null && AstUtils.IsSimpleInlineableExpr(vd.Initializer))
        {
            varName = vd.VarName;
            value = vd.Initializer;
            return true;
        }
        if (node is AstExpressionStatement es && es.Expression is AstAssignment assign &&
            assign.Target is AstIdentifier targetId && AstUtils.IsSimpleInlineableExpr(assign.Value))
        {
            varName = targetId.Name;
            value = assign.Value;
            return true;
        }

        return false;
    }

    private static bool CanInlineCandidate(string varName, AstExpression value, IList<AstNode> statements, int startIndex, HashSet<string> paramNames, Dictionary<string, int> assignmentCounts)
    {
        if (paramNames.Contains(varName) || varName.Contains('.'))
            return false;

        if (!assignmentCounts.TryGetValue(varName, out int assigns) || assigns != 1)
            return false;

        if (value is AstLiteral lit && lit.Value is string)
        {
            int useCount = 0;
            for (int j = startIndex; j < statements.Count; j++)
            {
                useCount += CountVariableUses(statements[j], varName);
            }
            if (useCount > 1)
                return false;
        }

        if (value is AstMemberAccess ma && ma.IsProperty)
        {
            for (int j = startIndex; j < statements.Count; j++)
            {
                if (IsPropertyMutated(statements[j], ma.MemberName))
                    return false;
            }
        }

        return true;
    }

    private sealed class InlineReplacer : Rosetta.Analysis.AST.Core.AstRewriter
    {
        private readonly string _varName;
        private readonly AstExpression _replacement;
        public bool Replaced { get; private set; }

        public InlineReplacer(string varName, AstExpression replacement)
        {
            _varName = varName;
            _replacement = replacement;
        }

        public override AstNode VisitIdentifier(AstIdentifier node)
        {
            if (node.Name == _varName)
            {
                Replaced = true;
                return AstUtils.CloneSimpleExpr(_replacement);
            }
            return base.VisitIdentifier(node);
        }
    }

    private static AstNode ReplaceInStmt(AstNode stmt, string varName, AstExpression replacement, ref bool replaced)
    {
        var replacer = new InlineReplacer(varName, replacement);
        var newNode = stmt.Accept(replacer);
        if (replacer.Replaced) replaced = true;
        return newNode ?? stmt;
    }

    private static void ReplaceInBlockInPlace(AstBlock block, string varName, AstExpression replacement, ref bool replaced, ref bool blockChanged)
    {
        for (int i = 0; i < block.Statements.Count; i++)
        {
            var oldStmt = block.Statements[i];
            var newStmt = ReplaceInStmt(oldStmt, varName, replacement, ref replaced);
            if (newStmt != oldStmt)
            {
                block.Statements[i] = newStmt;
                blockChanged = true;
            }
        }
    }

    private static AstExpression ReplaceInExpr(AstExpression expr, string varName, AstExpression replacement, ref bool replaced)
    {
        var replacer = new InlineReplacer(varName, replacement);
        var newExpr = (AstExpression)expr.Accept(replacer);
        if (replacer.Replaced) replaced = true;
        return newExpr;
    }

    private sealed class AssignmentCounter : Rosetta.Analysis.AST.Core.AstWalker
    {
        private readonly Dictionary<string, int> _counts;
        public AssignmentCounter(Dictionary<string, int> counts) { _counts = counts; }
        
        public override void VisitVariableDeclaration(AstVariableDeclaration node)
        {
            if (!_counts.ContainsKey(node.VarName)) _counts[node.VarName] = 0;
            _counts[node.VarName]++;
            base.VisitVariableDeclaration(node);
        }
        
        public override void VisitAssignment(AstAssignment node)
        {
            if (node.Target is AstIdentifier targetId)
            {
                if (!_counts.ContainsKey(targetId.Name)) _counts[targetId.Name] = 0;
                _counts[targetId.Name]++;
            }
            base.VisitAssignment(node);
        }
    }

    private static void CountAssignments(AstNode? node, Dictionary<string, int> counts)
    {
        if (node != null) node.Accept(new AssignmentCounter(counts));
    }

    private sealed class UseCounter : Rosetta.Analysis.AST.Core.AstWalker
    {
        private readonly string _varName;
        public int Count { get; private set; }
        public UseCounter(string varName) { _varName = varName; }

        public override void VisitIdentifier(AstIdentifier node)
        {
            if (node.Name == _varName) Count++;
            base.VisitIdentifier(node);
        }

        public override void VisitForeachStatement(AstForeachStatement node)
        {
            if (node.CollectionName == _varName) Count++;
            base.VisitForeachStatement(node);
        }

        public override void VisitNewExpression(AstNewExpression node)
        {
            if (node.Initializer != null && node.Initializer.Contains(_varName))
                Count++;
            base.VisitNewExpression(node);
        }
    }

    private static int CountVariableUses(AstNode? node, string varName)
    {
        if (node == null) return 0;
        var counter = new UseCounter(varName);
        node.Accept(counter);
        return counter.Count;
    }

    private sealed class PropertyMutationWalker : Rosetta.Analysis.AST.Core.AstWalker
    {
        private readonly string _propName;
        public bool Mutated { get; private set; }

        public PropertyMutationWalker(string propName) { _propName = propName; }

        public override void VisitAssignment(AstAssignment node)
        {
            if (node.Target is AstMemberAccess ma && ma.MemberName == _propName)
                Mutated = true;
            base.VisitAssignment(node);
        }
    }

    private static bool IsPropertyMutated(AstNode stmt, string propName)
    {
        var walker = new PropertyMutationWalker(propName);
        stmt.Accept(walker);
        return walker.Mutated;
    }

    private sealed class MutationCollector : Rosetta.Analysis.AST.Core.AstWalker
    {
        public List<AstExpression> MutatedExpressions { get; } = new();

        public override void VisitAssignment(AstAssignment node)
        {
            MutatedExpressions.Add(node.Target);
            base.VisitAssignment(node);
        }

        public override void VisitVariableDeclaration(AstVariableDeclaration node)
        {
            MutatedExpressions.Add(new AstIdentifier { Name = node.VarName });
            base.VisitVariableDeclaration(node);
        }

        public override void VisitUnaryExpression(AstUnaryExpression node)
        {
            if (node.Operator == "out " || node.Operator == "ref ")
            {
                MutatedExpressions.Add(node.Operand);
            }
            base.VisitUnaryExpression(node);
        }
    }

    private static AstExpression NormalizeGuard(AstExpression expr)
    {
        if (expr is AstBinaryExpression bin && bin.Operator == "!=")
        {
            if (bin.Right is AstLiteral lit && lit.Value == null)
                return bin.Left;
            if (bin.Left is AstLiteral litL && litL.Value == null)
                return bin.Right;
        }
        return expr;
    }

    private static bool IsKnownTrueCondition(AstExpression cond, List<AstExpression> knownTrue)
    {
        var normCond = NormalizeGuard(cond);
        foreach (var kt in knownTrue)
        {
            if (AreExpressionsEqual(normCond, kt))
                return true;
        }
        return false;
    }

    private static bool IsGuardCandidate(AstExpression expr)
    {
        var norm = NormalizeGuard(expr);
        if (norm is AstIdentifier) return true;
        if (norm is AstMemberAccess ma)
        {
            return IsGuardCandidate(ma.Target);
        }
        return false;
    }

    private static bool ContainsExpression(AstExpression? parent, AstExpression child)
    {
        if (parent == null) return false;
        if (AreExpressionsEqual(parent, child)) return true;

        switch (parent)
        {
            case AstMemberAccess ma:
                return ContainsExpression(ma.Target, child);
            case AstBinaryExpression bin:
                return ContainsExpression(bin.Left, child) || ContainsExpression(bin.Right, child);
            case AstUnaryExpression un:
                return ContainsExpression(un.Operand, child);
            case AstCastExpression cast:
                return ContainsExpression(cast.Operand, child);
            case AstIndexAccess idx:
                return ContainsExpression(idx.Target, child) || ContainsExpression(idx.Index, child);
            case AstMemoryAccess mem:
                return ContainsExpression(mem.Base, child);
            case AstCallExpression call:
                foreach (var arg in call.Arguments)
                {
                    if (ContainsExpression(arg, child)) return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static AstExpression? GetNullGuardVariable(AstExpression expr, string op)
    {
        if (expr is AstBinaryExpression bin && bin.Operator == op)
        {
            if (bin.Right is AstLiteral lit && lit.Value == null)
                return bin.Left;
            if (bin.Left is AstLiteral litL && litL.Value == null)
                return bin.Right;
        }
        return null;
    }

    private static bool AreExpressionsEqual(AstExpression? a, AstExpression? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        if (a.GetType() != b.GetType()) return false;

        switch (a)
        {
            case AstIdentifier idA:
                return idA.Name == ((AstIdentifier)b).Name;

            case AstLiteral litA:
                var litB = (AstLiteral)b;
                if (litA.Value == null) return litB.Value == null;
                return litA.Value.Equals(litB.Value);

            case AstMemberAccess maA:
                var maB = (AstMemberAccess)b;
                return maA.MemberName == maB.MemberName && 
                       maA.IsProperty == maB.IsProperty &&
                       AreExpressionsEqual(maA.Target, maB.Target);

            case AstBinaryExpression binA:
                var binB = (AstBinaryExpression)b;
                return binA.Operator == binB.Operator &&
                       AreExpressionsEqual(binA.Left, binB.Left) &&
                       AreExpressionsEqual(binA.Right, binB.Right);

            case AstUnaryExpression unA:
                var unB = (AstUnaryExpression)b;
                return unA.Operator == unB.Operator &&
                       AreExpressionsEqual(unA.Operand, unB.Operand);

            case AstCastExpression castA:
                var castB = (AstCastExpression)b;
                return castA.TypeName == castB.TypeName &&
                       AreExpressionsEqual(castA.Operand, castB.Operand);

            case AstIndexAccess idxA:
                var idxB = (AstIndexAccess)b;
                return AreExpressionsEqual(idxA.Target, idxB.Target) &&
                       AreExpressionsEqual(idxA.Index, idxB.Index);

            case AstMemoryAccess memA:
                var memB = (AstMemoryAccess)b;
                return memA.Offset == memB.Offset &&
                       AreExpressionsEqual(memA.Base, memB.Base);

            case AstCallExpression callA:
                var callB = (AstCallExpression)b;
                if (callA.MethodName != callB.MethodName || callA.Arguments.Count != callB.Arguments.Count)
                    return false;
                for (int i = 0; i < callA.Arguments.Count; i++)
                {
                    if (!AreExpressionsEqual(callA.Arguments[i], callB.Arguments[i]))
                        return false;
                }
                return true;

            default:
                return false;
        }
    }
}