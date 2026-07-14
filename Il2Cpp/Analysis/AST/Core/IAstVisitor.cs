namespace Rosetta.Analysis.AST;

/// <summary>
/// Defines a visitor for AST nodes that returns no value.
/// </summary>
public interface IAstVisitor
{
    void VisitAssignment(AstAssignment node);
    void VisitBinaryExpression(AstBinaryExpression node);
    void VisitBlock(AstBlock node);
    void VisitBreakStatement(AstBreakStatement node);
    void VisitCallExpression(AstCallExpression node);
    void VisitCastExpression(AstCastExpression node);
    void VisitContinueStatement(AstContinueStatement node);
    void VisitExpressionStatement(AstExpressionStatement node);
    void VisitForStatement(AstForStatement node);
    void VisitForeachStatement(AstForeachStatement node);
    void VisitIdentifier(AstIdentifier node);
    void VisitIfStatement(AstIfStatement node);
    void VisitIndexAccess(AstIndexAccess node);
    void VisitLiteral(AstLiteral node);
    void VisitMemberAccess(AstMemberAccess node);
    void VisitMemoryAccess(AstMemoryAccess node);
    void VisitNewExpression(AstNewExpression node);
    void VisitReturnStatement(AstReturnStatement node);
    void VisitUnaryExpression(AstUnaryExpression node);
    void VisitVariableDeclaration(AstVariableDeclaration node);
    void VisitWhileStatement(AstWhileStatement node);
}

/// <summary>
/// Defines a visitor for AST nodes that returns a value of type T.
/// </summary>
public interface IAstVisitor<out T>
{
    T VisitAssignment(AstAssignment node);
    T VisitBinaryExpression(AstBinaryExpression node);
    T VisitBlock(AstBlock node);
    T VisitBreakStatement(AstBreakStatement node);
    T VisitCallExpression(AstCallExpression node);
    T VisitCastExpression(AstCastExpression node);
    T VisitContinueStatement(AstContinueStatement node);
    T VisitExpressionStatement(AstExpressionStatement node);
    T VisitForStatement(AstForStatement node);
    T VisitForeachStatement(AstForeachStatement node);
    T VisitIdentifier(AstIdentifier node);
    T VisitIfStatement(AstIfStatement node);
    T VisitIndexAccess(AstIndexAccess node);
    T VisitLiteral(AstLiteral node);
    T VisitMemberAccess(AstMemberAccess node);
    T VisitMemoryAccess(AstMemoryAccess node);
    T VisitNewExpression(AstNewExpression node);
    T VisitReturnStatement(AstReturnStatement node);
    T VisitUnaryExpression(AstUnaryExpression node);
    T VisitVariableDeclaration(AstVariableDeclaration node);
    T VisitWhileStatement(AstWhileStatement node);
}
