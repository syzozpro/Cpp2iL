using System.Collections.Generic;
using System.Text;
using Rosetta.Common;
using Rosetta.Metadata;
using Rosetta.Model;

namespace Rosetta.Pipeline.Stages;

/// <summary>
/// Builds C# class/struct/interface/enum header declarations from IL2CPP metadata.
///
/// Examples:
///   "public class TestCoroutines : MonoBehaviour"
///   "private class GenericContainer&lt;T&gt;"
///   "public abstract class BaseManager : MonoBehaviour, IDisposable"
///   "public struct MyStruct : IEquatable&lt;MyStruct&gt;"
///   "public interface IGeneric&lt;T&gt;"
///
/// Extracted from CodeGenStage for single-responsibility.
/// </summary>
public static class ClassHeaderBuilder
{
    /// <summary>Build a full C# type header from metadata.</summary>
    public static string Build(string fullTypeName, string shortName, Dictionary<string, TypeDefinition> typeNameToTd, Il2CppContext context)
    {
        if (!typeNameToTd.TryGetValue(fullTypeName, out var td))
            return $"public class {shortName}";

        var sb = new StringBuilder();

        // Access modifier from ECMA-335 TypeAttributes
        sb.Append(GetAccessModifier(td));
        sb.Append(' ');

        // Abstract / sealed / static
        bool isAbstract = (td.Flags & 0x0080) != 0;
        bool isSealed = (td.Flags & 0x0100) != 0;
        bool isInterface = (td.Flags & 0x0020) != 0;

        if (isAbstract && isSealed && !isInterface)
            sb.Append("static ");
        else if (isAbstract && !isInterface)
            sb.Append("abstract ");
        else if (isSealed && !td.IsValueType && !isInterface)
            sb.Append("sealed ");

        // Type kind
        if (isInterface)
            sb.Append("interface ");
        else if (td.IsEnum)
            sb.Append("enum ");
        else if (td.IsStruct)
            sb.Append("struct ");
        else
            sb.Append("class ");

        // Type name — strip backtick arity
        string cleanName = shortName;
        int backtick = cleanName.IndexOf('`');
        if (backtick >= 0)
            cleanName = cleanName[..backtick];
        sb.Append(cleanName);

        // Generic parameters
        string? genericParams = ResolveGenericParams(td, context.Metadata!);
        if (genericParams != null)
            sb.Append(genericParams);

        // Base class and interfaces
        string? inheritance = ResolveInheritance(td, context);
        if (inheritance != null)
        {
            sb.Append(" : ");
            sb.Append(inheritance);
        }

        return sb.ToString();
    }

    /// <summary>Resolve generic type parameters: &lt;T&gt;, &lt;T, U&gt;, etc.</summary>
    public static string? ResolveGenericParams(TypeDefinition td, MetadataParser metadata)
    {
        if (td.GenericContainerIndex < 0 ||
            td.GenericContainerIndex >= metadata.GenericContainers.Length)
            return null;

        var container = metadata.GenericContainers[td.GenericContainerIndex];
        if (container.Count <= 0) return null;

        var names = new string[container.Count];
        for (int i = 0; i < container.Count; i++)
        {
            int paramIdx = container.ParameterStart + i;
            if (paramIdx >= 0 && paramIdx < metadata.GenericParameters.Length)
                names[i] = metadata.GenericParameters[paramIdx].Name ?? $"T{i}";
            else
                names[i] = $"T{i}";
        }

        return $"<{string.Join(", ", names)}>";
    }

    /// <summary>
    /// Resolve base class and interfaces into a single inheritance string.
    /// Skips System.Object/System.ValueType/System.Enum (implicit in C#).
    /// </summary>
    public static string? ResolveInheritance(TypeDefinition td, Il2CppContext context)
    {
        var parts = new List<string>();

        if (td.IsEnum && context.Metadata != null && context.TypeResolver != null)
        {
            string? underlyingType = ResolveEnumUnderlyingType(td, context);
            if (underlyingType != null && underlyingType != "int")
                parts.Add(underlyingType);
        }
        else
        {
            if (td.ParentIndex >= 0 && context.TypeResolver != null)
            {
                string baseName = context.TypeResolver.ResolveTypeName(td.ParentIndex);
                if (!IsImplicitBase(baseName, td))
                    parts.Add(TypeUtils.CleanTypeName(baseName));
            }
        }

        if (td.InterfacesCount > 0 && context.Metadata != null && context.TypeResolver != null)
        {
            for (int i = 0; i < td.InterfacesCount; i++)
            {
                int slot = td.InterfacesStart + i;
                if (slot < 0 || slot >= context.Metadata.Interfaces.Length) continue;

                int ifaceTypeIdx = context.Metadata.Interfaces[slot];
                string ifaceName = context.TypeResolver.ResolveTypeName(ifaceTypeIdx);
                parts.Add(TypeUtils.CleanTypeName(ifaceName));
            }
        }

        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>Resolve enum underlying type from the "value__" field.</summary>
    private static string? ResolveEnumUnderlyingType(TypeDefinition td, Il2CppContext context)
    {
        if (context.Metadata == null || context.TypeResolver == null) return null;

        for (int f = 0; f < td.FieldCount; f++)
        {
            int fieldIdx = td.FieldStart + f;
            if (fieldIdx < 0 || fieldIdx >= context.Metadata.FieldDefinitions.Length) continue;

            var fd = context.Metadata.FieldDefinitions[fieldIdx];
            if (fd.Name == "value__")
                return context.TypeResolver.ResolveTypeName(fd.TypeIndex);
        }

        return null;
    }

    /// <summary>Check if a base class name is implicit and should be omitted.</summary>
    private static bool IsImplicitBase(string baseName, TypeDefinition td)
    {
        return baseName is "System.Object" or "object" or "System.ValueType" or "System.Enum"
            || (td.IsValueType && baseName == "System.ValueType")
            || (td.IsEnum && baseName == "System.Enum");
    }

    /// <summary>Get C# access modifier from ECMA-335 TypeAttributes flags.</summary>
    public static string GetAccessModifier(TypeDefinition td)
    {
        uint visibility = td.Flags & 0x07;
        return visibility switch
        {
            0x00 => "internal",
            0x01 => "public",
            0x02 => "public",
            0x03 => "private",
            0x04 => "protected",
            0x05 => "internal",
            0x06 => "protected internal",
            0x07 => "protected internal",
            _ => "public"
        };
    }

    /// <summary>Get access modifier string from ECMA-335 MethodAttributes flags.</summary>
    public static string GetMethodAccess(ushort flags)
    {
        return (flags & 0x07) switch
        {
            0x01 => "private ",
            0x02 => "private protected ",
            0x03 => "internal ",
            0x04 => "protected ",
            0x05 => "protected internal ",
            0x06 => "public ",
            _ => "private "
        };
    }

    /// <summary>Get access modifier string from field access flags.</summary>
    public static string GetFieldAccessStr(FieldDefinition fd)
    {
        return fd.FieldAccess switch
        {
            0x01 => "private ",
            0x02 => "private protected ",
            0x03 => "internal ",
            0x04 => "protected ",
            0x05 => "protected internal ",
            0x06 => "public ",
            _ => "private "
        };
    }
}
