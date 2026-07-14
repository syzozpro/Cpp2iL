using System.Collections.Generic;

namespace Rosetta.Analysis.AST;

/// <summary>A sequence of statements (method body, if-body, loop-body, etc.).</summary>
public sealed class AstBlock : AstNode
{
    public List<AstNode> Statements { get; } = new();

    public override void Accept(IAstVisitor visitor) => visitor.VisitBlock(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitBlock(this);
}
