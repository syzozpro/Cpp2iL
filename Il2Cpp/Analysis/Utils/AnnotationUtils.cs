namespace Rosetta.Analysis.Utils;

/// <summary>
/// Parses IL2CPP annotation metadata at call sites.
/// </summary>
public static class AnnotationParser
{
    public record ParsedAnnotation(
        string FullAnnotation,
        string? TypeName,
        string? MethodName,
        bool IsStatic
    );

    public static ParsedAnnotation Parse(string annotation)
    {
        string current = annotation;
        
        // Unwrap generic_invoke(MethodRef(...)) → the inner method signature
        if (current.StartsWith("generic_invoke(MethodRef(") && current.EndsWith("))"))
        {
            current = current["generic_invoke(MethodRef(".Length..^2];
        }
        else if (current.StartsWith("MethodRef(") && current.EndsWith(")"))
        {
            current = current["MethodRef(".Length..^1];
        }

        // Strip [HFA:...] prefix which may appear after [M:...]
        if (current.StartsWith("[HFA:"))
        {
            int closeBracket = current.IndexOf(']');
            if (closeBracket > 0)
            {
                current = current[(closeBracket + 1)..].TrimStart();
            }
        }

        bool isStatic = current.StartsWith("static ");
        if (isStatic)
            current = current[7..];

        string methodName = current;
        string? typeName = null;
        int colonIdx = current.IndexOf("::");
        if (colonIdx > 0)
        {
            typeName = current[..colonIdx];
            methodName = current[(colonIdx + 2)..];
        }

        return new ParsedAnnotation(current, typeName, methodName, isStatic);
    }
}
