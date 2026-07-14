namespace Rosetta.Analysis.AST;

/// <summary>Base class for all AST nodes.</summary>
public abstract class AstNode 
{ 
    public abstract void Accept(IAstVisitor visitor);
    public abstract T Accept<T>(IAstVisitor<T> visitor);
}
