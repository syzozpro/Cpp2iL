namespace Rosetta.Lifter.Models;

/// <summary>Result of lifting a method body.</summary>
public sealed class LiftedMethod
{
    public string? MethodName { get; init; }
    public string? DeclaringType { get; init; }
    public bool IsStatic { get; init; }
    public List<string> Lines { get; init; } = new();
    public int ParameterCount { get; init; }
}
