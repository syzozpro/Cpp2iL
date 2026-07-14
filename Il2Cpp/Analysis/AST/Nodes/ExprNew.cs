using System.Collections.Generic;

namespace Rosetta.Analysis.AST;

/// <summary>Array/object creation (new Type[size] or new Type(args) or new Type[] { ... }).</summary>
public sealed class ExprNew : ExprNode
{
    public string TypeName { get; }
    public ExprNode? Size { get; }  // for arrays: new T[size]
    public List<ExprNode> Args { get; } // for objects: new T(args)

    /// <summary>Array literal initializer values. When set, emits new T[] { v1, v2, ... }.</summary>
    public List<string>? Initializer { get; set; }

    /// <summary>True for multi-dimensional arrays (e.g., new int[3, 3]).</summary>
    public bool IsMultiDim { get; set; }

    public ExprNew(string typeName, ExprNode? size = null, List<ExprNode>? args = null)
    {
        TypeName = typeName; Size = size; Args = args ?? new();
    }

    public override string Emit()
    {
        if (TypeName is "decimal" or "System.Decimal" && Args.Count == 5 && Initializer == null)
        {
            try
            {
                if (Args[0] is ExprLiteral loLit && loLit.Value is System.IConvertible loConv &&
                    Args[1] is ExprLiteral midLit && midLit.Value is System.IConvertible midConv &&
                    Args[2] is ExprLiteral hiLit && hiLit.Value is System.IConvertible hiConv &&
                    Args[3] is ExprLiteral signLit && signLit.Value is System.IConvertible signConv &&
                    Args[4] is ExprLiteral scaleLit && scaleLit.Value is System.IConvertible scaleConv)
                {
                    int lo = loConv.ToInt32(null);
                    int mid = midConv.ToInt32(null);
                    int hi = hiConv.ToInt32(null);
                    bool isNegative = signConv.ToInt32(null) != 0;
                    byte scale = scaleConv.ToByte(null);
                    decimal d = new decimal(lo, mid, hi, isNegative, scale);
                    return d.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m";
                }
            }
            catch { }
        }

        if (TypeName.EndsWith("?") && Args.Count == 1)
        {
            return Args[0].Emit();
        }
        if (TypeName.StartsWith("Nullable<") && Args.Count == 1)
        {
            return Args[0].Emit();
        }
        if (TypeName.StartsWith("System.Nullable<") && Args.Count == 1)
        {
            return Args[0].Emit();
        }

        if (TypeName.StartsWith("ValueTuple<") || TypeName.StartsWith("System.ValueTuple<"))
        {
            return $"({string.Join(", ", Args.ConvertAll(a => a.Emit()))})";
        }

        if (Initializer != null)
            return $"new {TypeName}[] {{ {string.Join(", ", Initializer)} }}";
        if (Size != null)
        {
            int firstBracket = TypeName.IndexOf('[');
            if (firstBracket >= 0)
            {
                string baseName = TypeName[..firstBracket];
                string suffix = TypeName[firstBracket..];
                return $"new {baseName}[{Size.Emit()}]{suffix}";
            }
            return $"new {TypeName}[{Size.Emit()}]";
        }
        return $"new {TypeName}({string.Join(", ", Args.ConvertAll(a => a.Emit()))})";
    }
}
