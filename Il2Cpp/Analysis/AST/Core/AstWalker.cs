using System.Collections.Generic;

namespace Rosetta.Analysis.AST.Core;

/// <summary>
/// A base visitor that recursively visits all AST nodes without modifying them.
/// Override specific Visit methods to perform analysis.
/// </summary>
public abstract class AstWalker : IAstVisitor
{
    public virtual void VisitAssignment(AstAssignment node)
    {
        node.Target.Accept(this);
        node.Value.Accept(this);
    }

    public virtual void VisitBinaryExpression(AstBinaryExpression node)
    {
        node.Left.Accept(this);
        node.Right.Accept(this);
    }

    public virtual void VisitBlock(AstBlock node)
    {
        foreach (var stmt in node.Statements)
        {
            stmt.Accept(this);
        }
    }

    public virtual void VisitBreakStatement(AstBreakStatement node) { }

    public virtual void VisitCallExpression(AstCallExpression node)
    {
        node.Target?.Accept(this);
        foreach (var arg in node.Arguments)
        {
            arg.Accept(this);
        }
    }

    public virtual void VisitCastExpression(AstCastExpression node)
    {
        node.Operand.Accept(this);
    }

    public virtual void VisitContinueStatement(AstContinueStatement node) { }

    public virtual void VisitExpressionStatement(AstExpressionStatement node)
    {
        node.Expression.Accept(this);
    }

    public virtual void VisitForStatement(AstForStatement node)
    {
        node.Init?.Accept(this);
        node.Condition?.Accept(this);
        node.Update?.Accept(this);
        node.Body.Accept(this);
    }

    public virtual void VisitForeachStatement(AstForeachStatement node)
    {
        node.Body.Accept(this);
    }

    public virtual void VisitIdentifier(AstIdentifier node) { }

    public virtual void VisitIfStatement(AstIfStatement node)
    {
        node.Condition.Accept(this);
        node.ThenBody.Accept(this);
        node.ElseBody?.Accept(this);
    }

    public virtual void VisitIndexAccess(AstIndexAccess node)
    {
        node.Target.Accept(this);
        node.Index.Accept(this);
    }

    public virtual void VisitLiteral(AstLiteral node) { }

    public virtual void VisitMemberAccess(AstMemberAccess node)
    {
        node.Target.Accept(this);
    }

    public virtual void VisitMemoryAccess(AstMemoryAccess node)
    {
        node.Base.Accept(this);
    }

    public virtual void VisitMethod(AstMethod node)
    {
        node.Body.Accept(this);
    }

    public virtual void VisitNewExpression(AstNewExpression node)
    {
        foreach (var arg in node.Arguments)
        {
            arg.Accept(this);
        }
    }

    public virtual void VisitReturnStatement(AstReturnStatement node)
    {
        node.Value?.Accept(this);
    }

    public virtual void VisitUnaryExpression(AstUnaryExpression node)
    {
        node.Operand.Accept(this);
    }

    public virtual void VisitVariableDeclaration(AstVariableDeclaration node)
    {
        node.Initializer?.Accept(this);
    }

    public virtual void VisitWhileStatement(AstWhileStatement node)
    {
        node.Condition.Accept(this);
        node.Body.Accept(this);
    }
}
