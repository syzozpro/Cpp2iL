using System.Collections.Generic;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Represents a structured method — the output of AstBuilder.
/// Contains a tree of AstNode instead of flat IR instructions or basic blocks.
/// </summary>
public sealed class AstMethod
{
    public string MethodName { get; init; } = "";
    public string? DeclaringType { get; init; }
    public string ReturnType { get; init; } = "void";
    public List<string> Parameters { get; init; } = new();
    public bool IsStatic { get; init; }
    public HashSet<string> OutVariableDeclarations { get; init; } = new();

    /// <summary>The root block — the method body as structured statements.</summary>
    public AstBlock Body { get; set; } = new();
}
