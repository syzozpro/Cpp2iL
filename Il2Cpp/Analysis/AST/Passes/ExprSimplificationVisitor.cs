using Rosetta.Analysis.AST.Core;
using Rosetta.Analysis.Utils;

namespace Rosetta.Analysis.AST.Passes;

/// <summary>
/// Rewrites and simplifies AST expressions.
/// </summary>
public sealed class ExprSimplificationVisitor : AstRewriter
{
    public override AstNode VisitUnaryExpression(AstUnaryExpression node)
    {
        var visited = base.VisitUnaryExpression(node);
        if (visited is AstUnaryExpression un)
        {
            // !!x -> x
            if (un.Operator == "!" && un.Operand is AstUnaryExpression un2 && un2.Operator == "!")
            {
                return un2.Operand;
            }

            // !(x == y) -> x != y
            if (un.Operator == "!" && un.Operand is AstBinaryExpression bin)
            {
                string? negatedOp = OpUtils.NegateOperator(bin.Operator);
                if (negatedOp != null)
                {
                    return new AstBinaryExpression
                    {
                        Operator = negatedOp,
                        Left = bin.Left,
                        Right = bin.Right
                    };
                }
            }
        }
        return visited;
    }
}
