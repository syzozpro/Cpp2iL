using System;
using System.Collections.Generic;
using Rosetta.Analysis.AST;
using Rosetta.Metadata;

namespace Rosetta.Model;

public sealed class ScriptAsset
{
    // ─── Identity ────────────────────────────────────────────────────────────

    /// <summary>Global index of this TypeDefinition in metadata.</summary>
    public int TypeIndex { get; init; }

    /// <summary>Short name (e.g. "PlayerController").</summary>
    public string Name { get; init; } = "";

    /// <summary>Namespace (e.g. "Game.Controllers").</summary>
    public string Namespace { get; init; } = "";

    /// <summary>Full qualified name: Namespace.Name or just Name.</summary>
    public string FullName => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";

    /// <summary>Reference to the raw TypeDefinition from metadata.</summary>
    public TypeDefinition TypeDef { get; init; } = null!;

    /// <summary>Namespaces imported via using directives.</summary>
    public List<string> Usings { get; } = new();

    // ─── Custom Attributes ──────────────────────────────────────────────────

    /// <summary>Pre-formatted C# attribute strings for this type (e.g. "[Serializable]").</summary>
    public List<string> Attributes { get; } = new();

    // ─── Type Classification ─────────────────────────────────────────────────

    /// <summary>ECMA-335 TypeAttributes flags.</summary>
    public uint Flags { get; init; }

    public bool IsInterface => (Flags & 0x0020) != 0;
    public bool IsAbstract => (Flags & 0x0080) != 0;
    public bool IsSealed => (Flags & 0x0100) != 0;
    public bool IsEnum => TypeDef.IsEnum;
    public bool IsStruct => TypeDef.IsStruct;
    public bool IsValueType => TypeDef.IsValueType;
    public bool IsStatic => IsAbstract && IsSealed && !IsInterface;
    public bool IsNested => TypeDef.DeclaringTypeIndex >= 0;

    // ─── Inheritance ─────────────────────────────────────────────────────────

    /// <summary>Base class name (null if System.Object / implicit).</summary>
    public string? BaseTypeName { get; set; }
    public ScriptAsset? BaseAsset;

    /// <summary>Implemented interface names.</summary>
    public List<string> InterfaceNames { get; } = new();

    // ─── Generics ────────────────────────────────────────────────────────────

    /// <summary>Generic type parameter names (e.g. ["T", "TValue"]).</summary>
    public List<string> GenericParameterNames { get; } = new();

    /// <summary>True if this type has generic parameters.</summary>
    public bool IsGeneric => GenericParameterNames.Count > 0;

    // ─── Fields ──────────────────────────────────────────────────────────────

    public List<FieldInfo> Fields { get; } = new();

    public sealed class FieldInfo
    {
        public int GlobalIndex { get; init; }
        public string Name { get; init; } = "";
        public string TypeName { get; init; } = "";
        /// <summary>Full namespace of the field's type (e.g. "UnityEngine"), before CleanTypeName strips it.</summary>
        public string TypeNamespace { get; init; } = "";
        public bool IsStatic { get; init; }
        public bool IsLiteral { get; init; }
        public bool IsReadOnly { get; init; }
        public uint AccessFlags { get; init; }
        public string? DefaultValue { get; set; }

        /// <summary>Reference to raw FieldDefinition.</summary>
        public FieldDefinition FieldDef { get; init; } = null!;

        /// <summary>Pre-formatted C# attribute strings (e.g. "[SerializeField]").</summary>
        public List<string> Attributes { get; } = new();
    }

    // ─── Properties ──────────────────────────────────────────────────────────

    public List<PropertyInfo> Properties { get; } = new();

    public sealed class PropertyInfo
    {
        public string Name { get; init; } = "";
        public string TypeName { get; init; } = "";
        public bool HasGetter { get; init; }
        public bool HasSetter { get; init; }
        public string? GetterMethodName { get; init; }
        public string? SetterMethodName { get; init; }
        public string GetterAccess { get; init; } = "public ";
        public string SetterAccess { get; init; } = "public ";
        public bool IsStatic { get; init; }
        public bool IsAutoProperty { get; init; }
        public string? BackingFieldName { get; init; }

        /// <summary>Reference to raw PropertyDef for token-based attribute lookup.</summary>
        public PropertyDef? PropDef { get; init; }

        /// <summary>Pre-formatted C# attribute strings.</summary>
        public List<string> Attributes { get; } = new();
    }

    // ─── Events ──────────────────────────────────────────────────────────────

    public List<EventInfo> Events { get; } = new();

    public sealed class EventInfo
    {
        public string Name { get; init; } = "";
        public string HandlerTypeName { get; init; } = "";
        public string? AddMethodName { get; init; }
        public string? RemoveMethodName { get; init; }
        public string? RaiseMethodName { get; init; }

        /// <summary>Reference to raw EventDef for token-based attribute lookup.</summary>
        public EventDef? EvtDef { get; init; }

        /// <summary>Pre-formatted C# attribute strings.</summary>
        public List<string> Attributes { get; } = new();
    }

    // ─── Methods ─────────────────────────────────────────────────────────────

    public List<MethodInfo> Methods { get; } = new();

    public sealed class MethodInfo
    {
        public int GlobalIndex { get; init; }
        public string Name { get; init; } = "";
        public string ReturnType { get; init; } = "void";
        public ushort Flags { get; init; }
        public bool IsStatic => (Flags & 0x0010) != 0;
        public bool IsVirtual => (Flags & 0x0040) != 0;
        public bool IsAbstract => (Flags & 0x0400) != 0;
        public ushort ParameterCount { get; init; }
        public List<ParameterInfo> Parameters { get; init; } = new();

        /// <summary>Reference to raw MethodDefinition.</summary>
        public MethodDefinition MethodDef { get; init; } = null!;

        /// <summary>Analysis result (IR/CFG/SSA) — populated by AnalysisStage.</summary>
        public Pipeline.MethodAnalysisResult? AnalysisResult { get; set; }

        /// <summary>Pre-formatted C# attribute strings (e.g. "[Obsolete]").</summary>
        public List<string> Attributes { get; } = new();

        /// <summary>Structured AST — populated by AstStage.</summary>
        public AstMethod? Ast { get; set; }

        /// <summary>Generated C# code — populated by CodeGen.</summary>
        public string? CSharpCode { get; set; }
    }

    public sealed class ParameterInfo
    {
        public string Name { get; init; } = "";
        public string TypeName { get; init; } = "";
    }

    // ─── Nested Types ────────────────────────────────────────────────────────

    /// <summary>Nested types as full ScriptAssets — recursively populated.</summary>
    public List<ScriptAsset> NestedTypes { get; } = new();

    // ─── Method Lookup ───────────────────────────────────────────────────────

    /// <summary>Set of method names to skip during emission (property accessors).</summary>
    public HashSet<string> SkipMethodNames { get; } = new();

    /// <summary>Field defaults extracted from .ctor/.cctor analysis.</summary>
    public Dictionary<string, string> FieldDefaults { get; } = new();

}

