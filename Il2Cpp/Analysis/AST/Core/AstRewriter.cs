using System.Collections.Generic;

namespace Rosetta.Analysis.AST.Core;

/// <summary>
/// A visitor that allows rewriting the AST.
/// Returns a new node if children were modified, or the same node if unchanged.
/// </summary>
public abstract class AstRewriter : IAstVisitor<AstNode>
{
    public virtual AstNode VisitAssignment(AstAssignment node)
    {
        var target = (AstExpression)node.Target.Accept(this);
        var value = (AstExpression)node.Value.Accept(this);
        if (target != node.Target || value != node.Value)
            return new AstAssignment { Target = target, Value = value };
        return node;
    }

    public virtual AstNode VisitBinaryExpression(AstBinaryExpression node)
    {
        var left = (AstExpression)node.Left.Accept(this);
        var right = (AstExpression)node.Right.Accept(this);
        if (left != node.Left || right != node.Right)
            return new AstBinaryExpression { Operator = node.Operator, Left = left, Right = right };
        return node;
    }

    public virtual AstNode VisitBlock(AstBlock node)
    {
        bool changed = false;
        var newStmts = new List<AstNode>(node.Statements.Count);
        foreach (var stmt in node.Statements)
        {
            var newStmt = stmt.Accept(this);
            if (newStmt != stmt) changed = true;
            if (newStmt != null) newStmts.Add(newStmt);
        }
        
        if (changed)
        {
            var newBlock = new AstBlock();
            newBlock.Statements.AddRange(newStmts);
            return newBlock;
        }
        return node;
    }

    public virtual AstNode VisitBreakStatement(AstBreakStatement node) => node;

    public virtual AstNode VisitCallExpression(AstCallExpression node)
    {
        var target = node.Target != null ? (AstExpression)node.Target.Accept(this) : null;
        bool changed = target != node.Target;
        
        var newArgs = new List<AstExpression>(node.Arguments.Count);
        foreach (var arg in node.Arguments)
        {
            var newArg = (AstExpression)arg.Accept(this);
            if (newArg != arg) changed = true;
            newArgs.Add(newArg);
        }
        
        if (changed)
            return new AstCallExpression { MethodName = node.MethodName, Target = target, Arguments = newArgs };
        return node;
    }

    public virtual AstNode VisitCastExpression(AstCastExpression node)
    {
        var op = (AstExpression)node.Operand.Accept(this);
        if (op != node.Operand)
            return new AstCastExpression { TypeName = node.TypeName, Operand = op };
        return node;
    }

    public virtual AstNode VisitContinueStatement(AstContinueStatement node) => node;

    public virtual AstNode VisitExpressionStatement(AstExpressionStatement node)
    {
        var expr = (AstExpression)node.Expression.Accept(this);
        if (expr != node.Expression)
            return new AstExpressionStatement { Expression = expr, Tag = node.Tag };
        return node;
    }

    public virtual AstNode VisitForStatement(AstForStatement node)
    {
        var init = node.Init?.Accept(this);
        var cond = node.Condition != null ? (AstExpression)node.Condition.Accept(this) : null;
        var update = node.Update?.Accept(this);
        var body = (AstBlock)node.Body.Accept(this);
        
        if (init != node.Init || cond != node.Condition || update != node.Update || body != node.Body)
        {
            return new AstForStatement {
                Init = init,
                Condition = cond,
                Update = update,
                Body = body
            };
        }
        return node;
    }

    public virtual AstNode VisitForeachStatement(AstForeachStatement node)
    {
        var body = (AstBlock)node.Body.Accept(this);
        if (body != node.Body)
            return new AstForeachStatement { ItemName = node.ItemName, CollectionName = node.CollectionName, Body = body };
        return node;
    }

    public virtual AstNode VisitIdentifier(AstIdentifier node) => node;

    public virtual AstNode VisitIfStatement(AstIfStatement node)
    {
        var cond = (AstExpression)node.Condition.Accept(this);
        var thenBody = (AstBlock)node.ThenBody.Accept(this);
        var elseBody = node.ElseBody != null ? (AstBlock)node.ElseBody.Accept(this) : null;
        
        if (cond != node.Condition || thenBody != node.ThenBody || elseBody != node.ElseBody)
            return new AstIfStatement { Condition = cond, ThenBody = thenBody, ElseBody = elseBody };
        return node;
    }

    public virtual AstNode VisitIndexAccess(AstIndexAccess node)
    {
        var target = (AstExpression)node.Target.Accept(this);
        var index = (AstExpression)node.Index.Accept(this);
        if (target != node.Target || index != node.Index)
            return new AstIndexAccess { Target = target, Index = index };
        return node;
    }

    public virtual AstNode VisitLiteral(AstLiteral node) => node;

    public virtual AstNode VisitMemberAccess(AstMemberAccess node)
    {
        var target = (AstExpression)node.Target.Accept(this);
        if (target != node.Target)
            return new AstMemberAccess { Target = target, MemberName = node.MemberName };
        return node;
    }

    public virtual AstNode VisitMemoryAccess(AstMemoryAccess node)
    {
        var b = (AstExpression)node.Base.Accept(this);
        if (b != node.Base)
            return new AstMemoryAccess { Base = b, Offset = node.Offset };
        return node;
    }

    public virtual AstNode VisitMethod(AstMethod node)
    {
        var body = (AstBlock)node.Body.Accept(this);
        if (body != node.Body)
        {
            node.Body = body;
        }
        return null!; // AstMethod is not an AstNode
    }

    public virtual AstNode VisitNewExpression(AstNewExpression node)
    {
        bool changed = false;
        var newArgs = new List<AstExpression>(node.Arguments.Count);
        foreach (var arg in node.Arguments)
        {
            var newArg = (AstExpression)arg.Accept(this);
            if (newArg != arg) changed = true;
            newArgs.Add(newArg);
        }
        
        if (changed)
            return new AstNewExpression { TypeName = node.TypeName, Arguments = newArgs, Initializer = node.Initializer };
        return node;
    }

    public virtual AstNode VisitReturnStatement(AstReturnStatement node)
    {
        var val = node.Value != null ? (AstExpression)node.Value.Accept(this) : null;
        if (val != node.Value)
            return new AstReturnStatement { Value = val };
        return node;
    }

    public virtual AstNode VisitUnaryExpression(AstUnaryExpression node)
    {
        var op = (AstExpression)node.Operand.Accept(this);
        if (op != node.Operand)
            return new AstUnaryExpression { Operator = node.Operator, Operand = op };
        return node;
    }

    public virtual AstNode VisitVariableDeclaration(AstVariableDeclaration node)
    {
        var init = node.Initializer != null ? (AstExpression)node.Initializer.Accept(this) : null;
        if (init != node.Initializer)
        {
            var n = new AstVariableDeclaration { TypeName = node.TypeName, VarName = node.VarName, Initializer = init, IsTypeResolved = node.IsTypeResolved };
            return n;
        }
        return node;
    }

    public virtual AstNode VisitWhileStatement(AstWhileStatement node)
    {
        var cond = (AstExpression)node.Condition.Accept(this);
        var body = (AstBlock)node.Body.Accept(this);
        if (cond != node.Condition || body != node.Body)
            return new AstWhileStatement { Condition = cond, Body = body };
        return node;
    }
}
