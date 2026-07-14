using System.Collections.Generic;

namespace Rosetta.Analysis.AST;

/// <summary>New object creation (new Type(args) or new Type[] { ... }).</summary>
public sealed class AstNewExpression : AstExpression
{
    public string TypeName { get; init; } = "";
    public List<AstExpression> Arguments { get; init; } = new();

    /// <summary>Array literal initializer. When set, emits new T[] { v1, v2, ... }.</summary>
    public List<string>? Initializer { get; init; }

    public override void Accept(IAstVisitor visitor) => visitor.VisitNewExpression(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitNewExpression(this);
}
