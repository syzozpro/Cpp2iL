namespace Rosetta.Analysis.AST;

/// <summary>Break statement (exits loop).</summary>
public sealed class AstBreakStatement : AstNode { 
    public override void Accept(IAstVisitor visitor) => visitor.VisitBreakStatement(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitBreakStatement(this);
}
