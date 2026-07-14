using Rosetta.Analysis.Utils;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

/// <summary>Normalizes scalar values recovered from IL2CPP boxing patterns.</summary>
public static class BoxingValueNormalizer
{
    public static ExprNode Normalize(string boxType, ExprNode value)
    {
        var originalValue = value;
        // Keep the existing hex-string fallback just in case
        value = ExprUtils.TryReinterpretAsFloat(value, boxType);

        if (value is ExprLiteral lit)
        {
            long rawValue = 0;
            bool isIntegral = false;
            
            switch (lit.Value)
            {
                case int i: rawValue = i; isIntegral = true; break;
                case long l: rawValue = l; isIntegral = true; break;
                case uint ui: rawValue = ui; isIntegral = true; break;
                case ulong ul: rawValue = unchecked((long)ul); isIntegral = true; break;
            }

            if (isIntegral)
            {
                switch (boxType)
                {
                    case "bool":
                    case "Boolean":
                        return new ExprLiteral(rawValue != 0);
                    case "byte":
                    case "Byte":
                        return new ExprLiteral(unchecked((byte)rawValue));
                    case "sbyte":
                    case "SByte":
                        return new ExprLiteral(unchecked((sbyte)rawValue));
                    case "short":
                    case "Int16":
                        return new ExprLiteral(unchecked((short)rawValue));
                    case "ushort":
                    case "UInt16":
                        return new ExprLiteral(unchecked((ushort)rawValue));
                    case "int":
                    case "Int32":
                        return new ExprLiteral(unchecked((int)rawValue));
                    case "uint":
                    case "UInt32":
                        return new ExprLiteral(unchecked((uint)rawValue));
                    case "long":
                    case "Int64":
                        return new ExprLiteral(rawValue);
                    case "ulong":
                    case "UInt64":
                        return new ExprLiteral(unchecked((ulong)rawValue));
                    case "char":
                    case "Char":
                        return new ExprLiteral(unchecked((char)rawValue));
                    case "float":
                    case "Single":
                        return new ExprLiteral(BitConverter.Int32BitsToSingle(unchecked((int)rawValue)));
                    case "double":
                    case "Double":
                        return new ExprLiteral(BitConverter.Int64BitsToDouble(rawValue));
                }
            }
        }

        if (value != originalValue && ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"BoxingValueNormalizer: Normalized '{originalValue.Emit()}' to '{value.Emit()}' for type '{boxType}'");
        }
        return value;
    }
}
