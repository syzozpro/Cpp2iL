using System.Collections.Generic;

namespace Rosetta.Analysis.AST;

internal sealed class AstDeclarationGatherer : IAstVisitor
{
    public List<AstVariableDeclaration> Declarations { get; } = new();

    public void VisitBlock(AstBlock node)
    {
        foreach (var stmt in node.Statements) stmt.Accept(this);
    }

    public void VisitExpressionStatement(AstExpressionStatement node) {}

    public void VisitIfStatement(AstIfStatement node)
    {
        node.ThenBody.Accept(this);
        node.ElseBody?.Accept(this);
    }

    public void VisitForStatement(AstForStatement node)
    {
        node.Init?.Accept(this);
        node.Body.Accept(this);
    }

    public void VisitWhileStatement(AstWhileStatement node)
    {
        node.Body.Accept(this);
    }

    public void VisitForeachStatement(AstForeachStatement node)
    {
        node.Body.Accept(this);
    }

    public void VisitVariableDeclaration(AstVariableDeclaration node)
    {
        Declarations.Add(node);
    }

    public void VisitAssignment(AstAssignment node) {}
    public void VisitBinaryExpression(AstBinaryExpression node) {}
    public void VisitCallExpression(AstCallExpression node) {}
    public void VisitCastExpression(AstCastExpression node) {}
    public void VisitContinueStatement(AstContinueStatement node) {}
    public void VisitBreakStatement(AstBreakStatement node) {}
    public void VisitIdentifier(AstIdentifier node) {}
    public void VisitIndexAccess(AstIndexAccess node) {}
    public void VisitLiteral(AstLiteral node) {}
    public void VisitMemberAccess(AstMemberAccess node) {}
    public void VisitMemoryAccess(AstMemoryAccess node) {}
    public void VisitNewExpression(AstNewExpression node) {}
    public void VisitReturnStatement(AstReturnStatement node) {}
    public void VisitUnaryExpression(AstUnaryExpression node) {}
}
