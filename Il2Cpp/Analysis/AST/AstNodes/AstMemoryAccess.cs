using System;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Structurally represents a raw memory dereference with an offset, e.g. *([Base + 0xOffset]).
/// Replaces the legacy string-flattened AstIdentifier approach for pointers.
/// </summary>
public sealed class AstMemoryAccess : AstExpression
{
    public AstExpression Base { get; init; } = null!;
    public long Offset { get; init; }


    public override void Accept(IAstVisitor visitor) => visitor.VisitMemoryAccess(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitMemoryAccess(this);
}
