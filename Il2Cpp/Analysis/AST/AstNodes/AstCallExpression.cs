using System.Collections.Generic;

namespace Rosetta.Analysis.AST;

/// <summary>Method call expression.</summary>
public sealed class AstCallExpression : AstExpression
{
    public string MethodName { get; init; } = "";
    public AstExpression? Target { get; set; }
    public List<AstExpression> Arguments { get; init; } = new();

    public override void Accept(IAstVisitor visitor) => visitor.VisitCallExpression(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitCallExpression(this);
}
