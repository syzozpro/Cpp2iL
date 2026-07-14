using System.Text;
using Rosetta.Analysis.AST.Core;
using Rosetta.Analysis.AST;

namespace Rosetta.CodeGen;

internal sealed class StatementEmitter : IAstVisitor
{
    private readonly StringBuilder _sb;
    private int _indent;

    public StatementEmitter(StringBuilder sb, int indent)
    {
        _sb = sb;
        _indent = indent;
    }

    private string Pad => new string(' ', _indent * 4);

    public void VisitReturnStatement(AstReturnStatement ret)
    {
        _sb.Append($"{Pad}return");
        if (ret.Value != null)
            _sb.Append($" {CSharpEmitter.EmitExpr(ret.Value)}");
        _sb.AppendLine(";");
    }

    public void VisitIfStatement(AstIfStatement ifs)
    {
        _sb.AppendLine($"{Pad}if ({CSharpEmitter.EmitExpr(ifs.Condition)})");
        _sb.AppendLine($"{Pad}{{");
        EmitBlockDirect(ifs.ThenBody, _indent + 1);
        _sb.AppendLine($"{Pad}}}");
        if (ifs.ElseBody != null)
        {
            _sb.AppendLine($"{Pad}else");
            _sb.AppendLine($"{Pad}{{");
            EmitBlockDirect(ifs.ElseBody, _indent + 1);
            _sb.AppendLine($"{Pad}}}");
        }
    }

    public void VisitWhileStatement(AstWhileStatement wh)
    {
        _sb.AppendLine($"{Pad}while ({CSharpEmitter.EmitExpr(wh.Condition)})");
        _sb.AppendLine($"{Pad}{{");
        EmitBlockDirect(wh.Body, _indent + 1);
        _sb.AppendLine($"{Pad}}}");
    }

    public void VisitForStatement(AstForStatement fs)
    {
        string initPart = "";
        if (fs.Init is AstVariableDeclaration vd2)
            initPart = $"{vd2.TypeName} {vd2.VarName}" + (vd2.Initializer != null ? $" = {CSharpEmitter.EmitExpr(vd2.Initializer)}" : "");
        else if (fs.Init is AstExpressionStatement initEs)
            initPart = CSharpEmitter.EmitExpr(initEs.Expression);
        var condPart = fs.Condition != null ? CSharpEmitter.EmitExpr(fs.Condition) : "";
        var updatePart = fs.Update is AstExpressionStatement updEs ? CSharpEmitter.EmitExpr(updEs.Expression) : "";
        _sb.AppendLine($"{Pad}for ({initPart}; {condPart}; {updatePart})");
        _sb.AppendLine($"{Pad}{{");
        EmitBlockDirect(fs.Body, _indent + 1);
        _sb.AppendLine($"{Pad}}}");
    }

    public void VisitForeachStatement(AstForeachStatement fe)
    {
        _sb.AppendLine($"{Pad}foreach (var {fe.ItemName} in {fe.CollectionName})");
        _sb.AppendLine($"{Pad}{{");
        EmitBlockDirect(fe.Body, _indent + 1);
        _sb.AppendLine($"{Pad}}}");
    }

    public void VisitBreakStatement(AstBreakStatement brk) => _sb.AppendLine($"{Pad}break;");
    public void VisitContinueStatement(AstContinueStatement cont) => _sb.AppendLine($"{Pad}continue;");

    public void VisitVariableDeclaration(AstVariableDeclaration vd)
    {
        _sb.Append($"{Pad}{vd.TypeName} {vd.VarName}");
        if (vd.Initializer != null)
            _sb.Append($" = {CSharpEmitter.EmitExpr(vd.Initializer)}");
        _sb.AppendLine(";");
    }

    public void VisitExpressionStatement(AstExpressionStatement es)
    {
        _sb.AppendLine($"{Pad}{CSharpEmitter.EmitExpr(es.Expression)};");
    }

    private void EmitBlockDirect(AstBlock block, int indent)
    {
        var emitter = new StatementEmitter(_sb, indent);
        foreach (var stmt in block.Statements) stmt.Accept(emitter);
    }

    public void VisitBlock(AstBlock node) => EmitBlockDirect(node, _indent);

    // Invalid statements
    public void VisitAssignment(AstAssignment node) { }
    public void VisitBinaryExpression(AstBinaryExpression node) { }
    public void VisitCallExpression(AstCallExpression node) { }
    public void VisitCastExpression(AstCastExpression node) { }
    public void VisitIdentifier(AstIdentifier node) { }
    public void VisitIndexAccess(AstIndexAccess node) { }
    public void VisitLiteral(AstLiteral node) { }
    public void VisitMemberAccess(AstMemberAccess node) { }
    public void VisitMemoryAccess(AstMemoryAccess node) { }
    public void VisitMethod(AstMethod node) { }
    public void VisitNewExpression(AstNewExpression node) { }
    public void VisitUnaryExpression(AstUnaryExpression node) { }
}
