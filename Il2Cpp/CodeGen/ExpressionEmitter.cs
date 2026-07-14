using System;
using Rosetta.Analysis.AST.Core;
using Rosetta.Analysis.AST;
using Rosetta.Pipeline;

namespace Rosetta.CodeGen;

internal sealed class ExpressionEmitter : IAstVisitor<string>
{
    private readonly bool _parentIsBinary;
    public ExpressionEmitter(bool parentIsBinary) { _parentIsBinary = parentIsBinary; }

    public string VisitLiteral(AstLiteral lit)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"          CSharpEmitter.EmitLiteral");
        return lit.Value switch
        {
            null => "null",
            string s => $"\"{Rosetta.Analysis.Utils.StringUtils.EscapeString(s)}\"",
            char c => $"'{(c == '\'' ? "\\'" : c.ToString())}'",
            bool b => b ? "true" : "false",
            float f => f.ToString("G", System.Globalization.CultureInfo.InvariantCulture) + "f",
            double d => d.ToString("G", System.Globalization.CultureInfo.InvariantCulture) + "d",
            uint ui => ui.ToString() + "u",
            long l => (l >= int.MinValue && l <= int.MaxValue) ? l.ToString() : l.ToString() + "L",
            ulong ul => ul.ToString() + "ul",
            decimal dec => dec.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m",
            _ => lit.Value.ToString() ?? "???"
        };
    }

    public string VisitIdentifier(AstIdentifier id) => id.Name;

    public string VisitBinaryExpression(AstBinaryExpression bin)
    {
        if (bin.Operator == "?" && bin.Right is AstBinaryExpression { Operator: ":" } colonExpr)
        {
            return MaybeParens($"{CSharpEmitter.EmitExpr(bin.Left)} ? {CSharpEmitter.EmitExpr(colonExpr.Left)} : {CSharpEmitter.EmitExpr(colonExpr.Right)}", _parentIsBinary);
        }
        return MaybeParens($"{CSharpEmitter.EmitExpr(bin.Left, true)} {bin.Operator} {CSharpEmitter.EmitExpr(bin.Right, true)}", _parentIsBinary);
    }

    public string VisitUnaryExpression(AstUnaryExpression un) => $"{un.Operator}{CSharpEmitter.EmitExpr(un.Operand, true)}";

    public string VisitMemoryAccess(AstMemoryAccess mem)
    {
        string baseExpr = CSharpEmitter.EmitExpr(mem.Base);
        if (mem.Offset == 0)
            return $"*({baseExpr})";

        string sign = mem.Offset < 0 ? "-" : "+";
        long absOffset = mem.Offset < 0 ? -mem.Offset : mem.Offset;
        return $"*([{baseExpr} {sign} 0x{absOffset:X}])";
    }

    public string VisitCallExpression(AstCallExpression call)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"          CSharpEmitter.EmitCall: {call.MethodName}");
        string args = string.Join(", ", call.Arguments.ConvertAll(a => CSharpEmitter.EmitExpr(a)));
        string methodName = call.MethodName;

        if (methodName == ".ctor" || methodName.EndsWith("::.ctor"))
        {
            if (CSharpEmitter.IsConstructorChainingCall(call, out _))
                return $"base({args})";

            string ctorType = "?";
            if (methodName.Contains("::"))
            {
                string rawType = methodName[..methodName.IndexOf("::")];
                string cleaned = CSharpEmitter.CleanDeclaringTypeName(rawType);
                if (!string.IsNullOrEmpty(cleaned))
                    ctorType = cleaned;
            }

            if (call.Target != null)
            {
                return $"{CSharpEmitter.EmitExpr(call.Target)} = new {ctorType}({args})";
            }
            return $"new {ctorType}({args})";
        }

        if (call.Target != null)
            return $"{CSharpEmitter.EmitExpr(call.Target)}.{methodName}({args})";
        return $"{methodName}({args})";
    }

    public string VisitMemberAccess(AstMemberAccess ma) => $"{CSharpEmitter.EmitExpr(ma.Target)}.{ma.MemberName}";
    public string VisitIndexAccess(AstIndexAccess ia) => $"{CSharpEmitter.EmitExpr(ia.Target)}[{CSharpEmitter.EmitExpr(ia.Index)}]";
    public string VisitCastExpression(AstCastExpression cast) => $"({cast.TypeName})({CSharpEmitter.EmitExpr(cast.Operand)})";
    public string VisitAssignment(AstAssignment assign) => $"{CSharpEmitter.EmitExpr(assign.Target)} = {CSharpEmitter.EmitExpr(assign.Value)}";
    
    public string VisitNewExpression(AstNewExpression ne)
    {
        if (ne.Initializer != null)
            return $"new {(ne.TypeName.EndsWith("[]") ? ne.TypeName : ne.TypeName + "[]")} {{ {string.Join(", ", ne.Initializer)} }}";
        if (ne.Arguments.Count > 0 && ne.Arguments[0] is AstAssignment)
            return $"new {ne.TypeName} {{ {string.Join(", ", ne.Arguments.ConvertAll(a => CSharpEmitter.EmitExpr(a)))} }}";

        int firstBracket = ne.TypeName.IndexOf('[');
        if (firstBracket >= 0 && ne.TypeName.EndsWith("]") && ne.Arguments.Count == 1)
        {
            string sizeStr = CSharpEmitter.EmitExpr(ne.Arguments[0]);
            string baseName = ne.TypeName[..firstBracket];
            string brackets = ne.TypeName[firstBracket..];
            
            if (string.IsNullOrEmpty(sizeStr) || sizeStr == "\"\"")
            {
                return $"new {baseName}{brackets}";
            }
            
            if (brackets.Contains(','))
            {
                return $"new {baseName}[{sizeStr}]{brackets[2..]}";
            }
            else
            {
                return $"new {baseName}[{sizeStr}]";
            }
        }
        return $"new {ne.TypeName}({string.Join(", ", ne.Arguments.ConvertAll(a => CSharpEmitter.EmitExpr(a)))})";
    }

    private static string MaybeParens(string inner, bool needsParens)
        => needsParens ? $"({inner})" : inner;

    // Invalid expressions
    public string VisitBlock(AstBlock node) => "???";
    public string VisitBreakStatement(AstBreakStatement node) => "???";
    public string VisitContinueStatement(AstContinueStatement node) => "???";
    public string VisitExpressionStatement(AstExpressionStatement node) => "???";
    public string VisitForStatement(AstForStatement node) => "???";
    public string VisitForeachStatement(AstForeachStatement node) => "???";
    public string VisitIfStatement(AstIfStatement node) => "???";
    public string VisitMethod(AstMethod node) => "???";
    public string VisitReturnStatement(AstReturnStatement node) => "???";
    public string VisitVariableDeclaration(AstVariableDeclaration node) => "???";
    public string VisitWhileStatement(AstWhileStatement node) => "???";
}
