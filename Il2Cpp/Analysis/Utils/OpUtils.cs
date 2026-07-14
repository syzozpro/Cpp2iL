namespace Rosetta.Analysis.Utils;

/// <summary>Utilities for manipulating and resolving binary and conditional operators.</summary>
public static class OpUtils
{
    /// <summary>Negate a logical or comparison operator.</summary>
    public static string? NegateOperator(string op) => op switch
    {
        "<=" => ">",
        ">=" => "<",
        "==" => "!=",
        "!=" => "==",
        "<"  => ">=",
        ">"  => "<=",
        _ => null
    };

    /// <summary>Convert an ARM condition code string (e.g. "eq", "lt", "hi") to its C# operator.</summary>
    public static string ConditionToOperator(string? condCode) => condCode switch
    {
        "eq" => "==", "ne" => "!=",
        "lt" => "<",  "le" => "<=",
        "gt" => ">",  "ge" => ">=",
        "hi" => ">",  "ls" => "<=",
        "hs" => ">=", "lo" => "<",
        _ => "!="
    };

    /// <summary>Convert an IL2CPP/C# operator method name (e.g. "op_LessThan") to its C# operator.</summary>
    public static string? MethodNameToOperator(string methodName) => methodName switch
    {
        "op_Equality" => "==",
        "op_Inequality" => "!=",
        "op_LessThan" => "<",
        "op_GreaterThan" => ">",
        "op_LessThanOrEqual" => "<=",
        "op_GreaterThanOrEqual" => ">=",
        "op_Addition" => "+",
        "op_Subtraction" => "-",
        "op_Multiply" => "*",
        "op_Division" => "/",
        "op_Modulus" => "%",
        "op_BitwiseAnd" => "&",
        "op_BitwiseOr" => "|",
        "op_ExclusiveOr" => "^",
        "op_LeftShift" => "<<",
        "op_RightShift" => ">>",
        "op_UnaryNegation" => "-",
        "op_LogicalNot" => "!",
        "op_OnesComplement" => "~",
        _ => null
    };
}
