namespace Rosetta.Analysis.AST;

/// <summary>Continue statement (skips to next iteration).</summary>
public sealed class AstContinueStatement : AstNode { 
    public override void Accept(IAstVisitor visitor) => visitor.VisitContinueStatement(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitContinueStatement(this);
}
