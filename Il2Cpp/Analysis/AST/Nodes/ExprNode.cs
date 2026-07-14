namespace Rosetta.Analysis.AST;

/// <summary>Base class for all expression tree nodes.</summary>
public abstract class ExprNode
{
    /// <summary>Inferred C# type (e.g., "string", "int", "string[]"). Null = unknown.</summary>
    public string? InferredType { get; set; }

    /// <summary>
    /// Static field hint: carries the owning static field name (e.g., "Vector3.forwardVector")
    /// for component loads. NOT emitted — used by box handler for struct reconstruction.
    /// </summary>
    public string? StaticFieldHint { get; set; }

    /// <summary>Exact TypeDefinition index carried by metadata-derived type expressions.</summary>
    public int MetadataTypeDefIndex { get; set; } = -1;

    /// <summary>Emit this expression as a C# string.</summary>
    public abstract string Emit();
}
