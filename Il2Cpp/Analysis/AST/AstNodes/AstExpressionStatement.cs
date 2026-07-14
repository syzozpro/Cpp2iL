namespace Rosetta.Analysis.AST;

/// <summary>A simple expression statement (assignment, call, etc.).</summary>
public sealed class AstExpressionStatement : AstNode
{
    public AstExpression Expression { get; init; } = null!;

    /// <summary>
    /// Semantic tag set by SsaAstBuilder to classify IL2CPP boilerplate patterns.
    /// Allows BoilerplatePruner to match on typed tags instead of string-parsing
    /// the flattened AstIdentifier.Name.
    /// </summary>
    public StatementTag Tag { get; set; } = StatementTag.None;

    public override void Accept(IAstVisitor visitor) => visitor.VisitExpressionStatement(this);
    public override T Accept<T>(IAstVisitor<T> visitor) => visitor.VisitExpressionStatement(this);
}

/// <summary>
/// Semantic classification of IL2CPP-generated statements.
/// Set by SsaAstBuilder during emission; consumed by BoilerplatePruner.
/// </summary>
public enum StatementTag
{
    /// <summary>Normal user code — no special handling.</summary>
    None = 0,

    /// <summary>IL2CPP metadata variable load (metadata_var).</summary>
    MetadataVar,

    /// <summary>IL2CPP metadata page store (*([il2cpp_metadata_page + N]) = 1).</summary>
    MetadataPageStore,

    /// <summary>typeof(T) expression load.</summary>
    TypeOf,

    /// <summary>vtable or static_fields load.</summary>
    VTableLoad,

    /// <summary>MethodRef(Type::Method) — IL2CPP method pointer load.</summary>
    MethodRef,

    /// <summary>Class initialization guard flag read.</summary>
    ClassInitFlag
}
