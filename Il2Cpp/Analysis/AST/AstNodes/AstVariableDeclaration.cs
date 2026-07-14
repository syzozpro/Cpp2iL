namespace Rosetta.Analysis.AST;

/// <summary>Variable declaration with optional initializer.</summary>
public sealed class AstVariableDeclaration : AstNode
{
    public string TypeName { get; set; } = "var";
    public bool IsTypeResolved { get; set; } = false;
    public string VarName { get; set; } = "";
    public AstExpression? Initializer { get; set; }

    public override void Accept(IAstVisitor visitor) => visitor.VisitVariableDeclaration(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitVariableDeclaration(this);
}
