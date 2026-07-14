using System.Text;
using System.Collections.Generic;
using Rosetta.Analysis.AST.Core;
using Rosetta.Analysis.AST;
using Rosetta.Pipeline;
using Rosetta.Model;

namespace Rosetta.CodeGen;

/// <summary>
/// Emits C# source code from an AstMethod.
/// 
/// Takes the structured AST and walks the tree to produce clean,
/// readable C# pseudocode output.
/// </summary>
public sealed class CSharpEmitter
{
    /// <summary>
    /// Emit a complete C# method from the AST.
    /// </summary>
    public string Emit(AstMethod method, string? accessModifier = null)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"CSharpEmitter.Emit: {method.DeclaringType}::{method.MethodName}");
        var sb = new StringBuilder();

        EmitMethodSignature(sb, method, accessModifier);

        AstExpressionStatement? baseCtorStmt = FindConstructorInitializer(method, out string? initializerCode);
        if (initializerCode != null)
        {
            sb.Append(initializerCode);
        }

        sb.AppendLine();
        sb.AppendLine("{");

        EmitBlock(sb, method.Body, indent: 1, skipStmt: baseCtorStmt);

        sb.AppendLine("}");
        return sb.ToString();
    }

    private void EmitMethodSignature(StringBuilder sb, AstMethod method, string? accessModifier)
    {
        bool isCtor = method.MethodName == ".ctor" || method.MethodName == ".cctor";
        if (isCtor)
        {
            string typeName = GetConstructorTypeName(method.DeclaringType);

            if (method.MethodName == ".cctor")
            {
                sb.Append($"static {typeName}(");
            }
            else
            {
                if (!string.IsNullOrEmpty(accessModifier))
                    sb.Append($"{accessModifier}");
                sb.Append($"{typeName}(");
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(accessModifier))
                sb.Append(accessModifier);
            if (method.IsStatic && !(accessModifier ?? "").Contains("static "))
                sb.Append("static ");
            sb.Append($"{method.ReturnType} {method.MethodName}(");
        }
        sb.Append(string.Join(", ", method.Parameters));
        sb.Append(")");
    }

    private string GetConstructorTypeName(string? declaringType)
    {
        string cleaned = CleanDeclaringTypeName(declaringType);
        return string.IsNullOrEmpty(cleaned) ? "Constructor" : cleaned;
    }

    internal static string CleanDeclaringTypeName(string? declaringType)
    {
        if (string.IsNullOrEmpty(declaringType))
            return "";

        int lastSep = System.Math.Max(declaringType.LastIndexOf('.'), declaringType.LastIndexOf('+'));
        string typeName = lastSep >= 0 ? declaringType[(lastSep + 1)..] : declaringType;
        int backtick = typeName.IndexOf('`');
        if (backtick >= 0) typeName = typeName[..backtick];
        return typeName;
    }

    private AstExpressionStatement? FindConstructorInitializer(AstMethod method, out string? initializerCode)
    {
        initializerCode = null;
        if (method.Body.Statements.Count == 0) return null;

        foreach (var stmt in method.Body.Statements)
        {
            if (stmt is AstExpressionStatement es)
            {
                if (IsConstructorChainingCall(es.Expression, out var call))
                {
                    string args = string.Join(", ", call.Arguments.ConvertAll(a => EmitExpr(a)));
                    bool isSelfCtor = !string.IsNullOrEmpty(method.DeclaringType) &&
                                       call.MethodName.StartsWith(method.DeclaringType + "::");
                    string ctorChaining = isSelfCtor ? "this" : "base";
                    initializerCode = $" : {ctorChaining}({args})";
                    return es;
                }
                else if (IsBaseConstructorIdentifier(es.Expression, out var chainCode))
                {
                    initializerCode = chainCode;
                    return es;
                }
            }
        }
        return null;
    }

    internal static bool IsConstructorChainingCall(AstExpression expr, out AstCallExpression call)
    {
        if (expr is AstCallExpression c && c.Target != null && EmitExpr(c.Target) == "this" && (c.MethodName == ".ctor" || c.MethodName.EndsWith("::.ctor")))
        {
            call = c;
            return true;
        }
        call = null!;
        return false;
    }

    public static bool IsConstructorEmpty(ScriptAsset.MethodInfo methodInfo)
    {
        // Skip empty parameterless constructors (or constructors containing only base() call)
        if (methodInfo.Name == ".ctor" && methodInfo.Parameters.Count == 0)
                return true;

        return false;
    }

    internal static bool IsBaseConstructorIdentifier(AstExpression expr, out string? chainCode)
    {
        if (expr is AstIdentifier id && (id.Name == "base()" || id.Name.StartsWith("base(")))
        {
            chainCode = id.Name == "base()" ? "" : $" : {id.Name}";
            return true;
        }

        if (expr is AstCallExpression call && call.MethodName == "base")
        {
            string args = string.Join(", ", call.Arguments.ConvertAll(a => EmitExpr(a)));
            chainCode = string.IsNullOrEmpty(args) ? "" : $" : base({args})";
            return true;
        }
        chainCode = null;
        return false;
    }

    private void EmitBlock(StringBuilder sb, AstBlock block, int indent, AstNode? skipStmt = null)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    CSharpEmitter.EmitBlock({block.Statements.Count} statements)");
        var emitter = new StatementEmitter(sb, indent);
        
        foreach (var stmt in block.Statements)
        {
            if (skipStmt != null && ReferenceEquals(stmt, skipStmt))
                continue;
            
            if (stmt is AstExpressionStatement es)
            {
                if (IsConstructorChainingCall(es.Expression, out _) || IsBaseConstructorIdentifier(es.Expression, out _))
                {
                    continue;
                }
            }
            
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      CSharpEmitter.EmitNode: {stmt.GetType().Name}");
            stmt.Accept(emitter);
        }
    }

    public string EmitExprPublic(AstExpression expr) => EmitExpr(expr);

    internal static string EmitExpr(AstExpression expr, bool parentIsBinary = false)
    {
        if (expr == null) return "???";
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        CSharpEmitter.EmitExpr: {expr.GetType().Name}");
        var emitter = new ExpressionEmitter(parentIsBinary);
        return expr.Accept(emitter);
    }

    private static string MaybeParens(string inner, bool needsParens) => needsParens ? $"({inner})" : inner;
}
