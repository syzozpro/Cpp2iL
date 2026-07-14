using System.Collections.Generic;
using Rosetta.Analysis.AST;
using Rosetta.Analysis.AST.Core;

namespace Rosetta.Analysis.AST.Utils;

public static class AstUtils
{
    private sealed class IdentifierCollector : AstWalker
    {
        public HashSet<string> Names { get; } = new();
        public override void VisitIdentifier(AstIdentifier node) { Names.Add(node.Name); }
        public override void VisitVariableDeclaration(AstVariableDeclaration node)
        {
            node.Initializer?.Accept(this);
        }
        public override void VisitAssignment(AstAssignment node)
        {
            if (node.Target is not AstIdentifier)
            {
                node.Target.Accept(this);
            }
            node.Value.Accept(this);
        }
    }

    public static bool IsSideEffectFree(AstExpression expr)
    {
        return expr switch
        {
            AstLiteral => true,
            AstIdentifier => true,
            AstMemberAccess ma => IsSideEffectFree(ma.Target),
            AstBinaryExpression bin => IsSideEffectFree(bin.Left) && IsSideEffectFree(bin.Right),
            AstUnaryExpression un => IsSideEffectFree(un.Operand),
            AstCastExpression cast => IsSideEffectFree(cast.Operand),
            AstIndexAccess ia => IsSideEffectFree(ia.Target) && IsSideEffectFree(ia.Index),
            _ => false
        };
    }

    public static string? GetIdentifierName(AstExpression? expr)
    {
        if (expr is AstIdentifier id) return id.Name;
        return null;
    }

    public static bool IsZeroOrNull(AstExpression expr)
    {
        if (expr is AstLiteral lit)
        {
            if (lit.Value is int i && i == 0) return true;
            if (lit.Value is long l && l == 0) return true;
            if (lit.Value == null) return true;
        }
        if (expr is AstIdentifier id && (id.Name == "0" || id.Name == "null"))
            return true;
        return false;
    }

    public static void CollectIdentifierNames(AstNode? node, HashSet<string> names)
    {
        if (node == null) return;
        var collector = new IdentifierCollector();
        node.Accept(collector);
        foreach (var n in collector.Names) names.Add(n);
    }

    public static void CollectExprIdentifiers(AstExpression? expr, HashSet<string> names)
    {
        if (expr == null) return;
        var collector = new IdentifierCollector();
        expr.Accept(collector);
        foreach (var n in collector.Names) names.Add(n);
    }

    public static AstExpression CloneSimpleExpr(AstExpression expr)
    {
        if (expr is AstIdentifier astIdentifier) return new AstIdentifier { Name = astIdentifier.Name };
        if (expr is AstLiteral astLiteral) return new AstLiteral { Value = astLiteral.Value };
        if (expr is AstMemberAccess astMemberAccess) return new AstMemberAccess {
            Target = CloneSimpleExpr(astMemberAccess.Target), MemberName = astMemberAccess.MemberName, IsProperty = astMemberAccess.IsProperty
        };
        if (expr is AstMemoryAccess astMemoryAccess) return new AstMemoryAccess {
            Base = CloneSimpleExpr(astMemoryAccess.Base), Offset = astMemoryAccess.Offset
        };
        return expr;
    }

    public static bool IsSimpleInlineableExpr(AstExpression expr)
    {
        if (expr is AstLiteral) return true;
        if (expr is AstIdentifier) return true;
        if (expr is AstMemberAccess) return true;
        if (expr is AstMemoryAccess) return true;
        if (expr is AstCastExpression astCastExpression) return IsSimpleInlineableExpr(astCastExpression.Operand);
        return false;
    }
}
