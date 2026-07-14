using System.Collections.Generic;

namespace Rosetta.Analysis.AST;

internal sealed class AstVariableRenamer : IAstVisitor
{
    private readonly Dictionary<string, string> _nameMapping;

    public AstVariableRenamer(Dictionary<string, string> nameMapping)
    {
        _nameMapping = nameMapping;
    }

    public void VisitBlock(AstBlock node)
    {
        foreach (var stmt in node.Statements) stmt.Accept(this);
    }

    public void VisitExpressionStatement(AstExpressionStatement node)
    {
        node.Expression.Accept(this);
    }

    public void VisitIfStatement(AstIfStatement node)
    {
        node.Condition.Accept(this);
        node.ThenBody.Accept(this);
        node.ElseBody?.Accept(this);
    }

    public void VisitForStatement(AstForStatement node)
    {
        node.Init?.Accept(this);
        node.Condition?.Accept(this);
        node.Update?.Accept(this);
        node.Body.Accept(this);
    }

    public void VisitWhileStatement(AstWhileStatement node)
    {
        node.Condition.Accept(this);
        node.Body.Accept(this);
    }

    public void VisitForeachStatement(AstForeachStatement node)
    {
        if (_nameMapping.TryGetValue(node.ItemName, out var newName))
        {
            node.ItemName = newName;
        }
        if (_nameMapping.TryGetValue(node.CollectionName, out var newColName))
        {
            node.CollectionName = newColName;
        }
        node.Body.Accept(this);
    }

    public void VisitVariableDeclaration(AstVariableDeclaration node)
    {
        if (_nameMapping.TryGetValue(node.VarName, out var newName))
        {
            node.VarName = newName;
        }
        node.Initializer?.Accept(this);
    }

    public void VisitAssignment(AstAssignment node)
    {
        node.Target.Accept(this);
        node.Value.Accept(this);
    }

    public void VisitIdentifier(AstIdentifier node)
    {
        if (_nameMapping.TryGetValue(node.Name, out var newName))
        {
            node.Name = newName;
        }
    }

    public void VisitBinaryExpression(AstBinaryExpression node)
    {
        node.Left.Accept(this);
        node.Right.Accept(this);
    }

    public void VisitCallExpression(AstCallExpression node)
    {
        node.Target?.Accept(this);
        foreach (var arg in node.Arguments) arg.Accept(this);
    }

    public void VisitCastExpression(AstCastExpression node)
    {
        node.Operand.Accept(this);
    }

    public void VisitIndexAccess(AstIndexAccess node)
    {
        node.Target.Accept(this);
        node.Index.Accept(this);
    }

    public void VisitMemberAccess(AstMemberAccess node)
    {
        node.Target.Accept(this);
    }

    public void VisitMemoryAccess(AstMemoryAccess node)
    {
        node.Base.Accept(this);
    }

    public void VisitNewExpression(AstNewExpression node)
    {
        foreach (var arg in node.Arguments) arg.Accept(this);
    }

    public void VisitReturnStatement(AstReturnStatement node)
    {
        node.Value?.Accept(this);
    }

    public void VisitUnaryExpression(AstUnaryExpression node)
    {
        node.Operand.Accept(this);
    }

    public void VisitBreakStatement(AstBreakStatement node) {}
    public void VisitContinueStatement(AstContinueStatement node) {}
    public void VisitLiteral(AstLiteral node) {}
}
