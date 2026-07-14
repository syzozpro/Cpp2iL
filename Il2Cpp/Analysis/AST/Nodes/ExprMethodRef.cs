namespace Rosetta.Analysis.AST;

/// <summary>IL2CPP MethodInfo* reference.</summary>
public sealed class ExprMethodRef : ExprNode
{
    public int MethodIndex { get; }
    public string MethodName { get; }

    public ExprMethodRef(int methodIndex, string methodName)
    {
        MethodIndex = methodIndex;
        MethodName = methodName;
    }

    public override string Emit() => $"MethodRef({MethodName})";
}
