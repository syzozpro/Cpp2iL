using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rosetta.Analysis.AST;
using Rosetta.CodeGen;
using Rosetta.Common;
using Rosetta.Metadata;
using Rosetta.Model;
using Rosetta.Config;

namespace Rosetta.Pipeline.Stages;

/// <summary>
/// Emits class member declarations: fields, auto-properties, non-auto properties,
/// enum members, and extracts field default values from constructors.
///
/// Extracted from CodeGenStage for single-responsibility.
/// </summary>
public static class MemberEmitter
{
    // ─── Property Info Model ────────────────────────────────────────────────

    /// <summary>Holds property information for a type.</summary>
    public class PropertyMap
    {
        /// <summary>Backing field name → auto-property entry.</summary>
        public Dictionary<string, PropEntry> ByBackingField { get; } = new();
        /// <summary>Non-auto properties (have getter/setter with custom body).</summary>
        public List<PropEntry> NonAutoProperties { get; } = new();
        /// <summary>Method names to skip (get_X, set_X).</summary>
        public HashSet<string> SkipMethods { get; } = new();
    }

    public record PropEntry(string Name, string Type, bool HasGet, bool HasSet,string? GetterMethodName, string? SetterMethodName,
        string GetterAccess, string SetterAccess, bool IsStatic, bool IsAutoProperty);

    // ─── Property Info Builder ──────────────────────────────────────────────

    /// <summary>Build property info from metadata PropertyDefinitions.</summary>
    public static PropertyMap BuildPropertyInfo(TypeDefinition td, Il2CppContext context)
    {
        var map = new PropertyMap();
        if (context.Metadata == null || context.TypeResolver == null) return map;

        var fieldNames = new HashSet<string>();
        for (int f = 0; f < td.FieldCount; f++)
        {
            int fieldIdx = td.FieldStart + f;
            if (fieldIdx >= 0 && fieldIdx < context.Metadata.FieldDefinitions.Length)
                fieldNames.Add(context.Metadata.FieldDefinitions[fieldIdx].Name ?? "");
        }

        for (int p = 0; p < td.PropertyCount; p++)
        {
            int propIdx = td.PropertyStart + p;
            if (propIdx < 0 || propIdx >= context.Metadata.PropertyDefinitions.Length) continue;

            var prop = context.Metadata.PropertyDefinitions[propIdx];
            string propName = prop.Name ?? $"Property_{p}";

            bool hasGet = prop.Get >= 0;
            bool hasSet = prop.Set >= 0;
            string? getterName = null;
            string? setterName = null;
            string propType = "object";
            string getterAccess = "public ";
            string setterAccess = "public ";
            bool isStatic = false;

            if (hasGet)
            {
                int getMethodIdx = td.MethodStart + prop.Get;
                if (getMethodIdx >= 0 && getMethodIdx < context.Metadata.MethodDefinitions.Length)
                {
                    var getMd = context.Metadata.MethodDefinitions[getMethodIdx];
                    getterName = getMd.Name ?? $"get_{propName}";
                    propType = TypeUtils.CleanTypeName(context.TypeResolver.ResolveTypeName(getMd.ReturnTypeIndex));
                    getterAccess = ClassHeaderBuilder.GetMethodAccess(getMd.Flags);
                    isStatic = (getMd.Flags & 0x0010) != 0;
                }
            }

            if (hasSet)
            {
                int setMethodIdx = td.MethodStart + prop.Set;
                if (setMethodIdx >= 0 && setMethodIdx < context.Metadata.MethodDefinitions.Length)
                {
                    var setMd = context.Metadata.MethodDefinitions[setMethodIdx];
                    setterName = setMd.Name ?? $"set_{propName}";
                    setterAccess = ClassHeaderBuilder.GetMethodAccess(setMd.Flags);
                    if (!hasGet && setMd.ParameterCount > 0)
                    {
                        int paramIdx = setMd.ParameterStart;
                        if (paramIdx >= 0 && paramIdx < context.Metadata.ParameterDefinitions.Length)
                            propType = TypeUtils.CleanTypeName(context.TypeResolver.ResolveTypeName(
                                context.Metadata.ParameterDefinitions[paramIdx].TypeIndex));
                    }
                }
            }

            string backingField = $"<{propName}>k__BackingField";
            bool isAuto = fieldNames.Contains(backingField);

            var entry = new PropEntry(propName, propType, hasGet, hasSet,
                getterName, setterName, getterAccess, setterAccess, isStatic, isAuto);

            if (isAuto)
                map.ByBackingField[backingField] = entry;
            else
                map.NonAutoProperties.Add(entry);

            if (getterName != null) map.SkipMethods.Add(getterName);
            if (setterName != null) map.SkipMethods.Add(setterName);
        }

        return map;
    }

    // ─── Field Emission ─────────────────────────────────────────────────────

    /// <summary>Emit class field declarations. Backing fields become auto-properties.</summary>
    public static void EmitFields(StringBuilder sb, TypeDefinition td,
        Il2CppContext context, string indent, Dictionary<string, string>? defaults = null,
        PropertyMap? propInfo = null)
    {
        if (context.Metadata == null || context.TypeResolver == null) return;
        if (td.FieldCount <= 0) return;

        string fieldIndent = indent + "    ";
        bool emittedAny = false;

        var defaultLookup = new Dictionary<int, FieldDefaultValueDef>();
        foreach (var fdv in context.Metadata.FieldDefaultValues)
            defaultLookup[fdv.FieldIndex] = fdv;

        for (int f = 0; f < td.FieldCount; f++)
        {
            int fieldIdx = td.FieldStart + f;
            if (fieldIdx < 0 || fieldIdx >= context.Metadata.FieldDefinitions.Length) continue;

            var fd = context.Metadata.FieldDefinitions[fieldIdx];
            string fieldName = fd.Name ?? $"field_{f}";

            if (fieldName == "value__") continue;

            // Auto-property
            if (propInfo != null && propInfo.ByBackingField.TryGetValue(fieldName, out var prop))
            {
                var psb = new StringBuilder();
                psb.Append(fieldIndent);
                psb.Append(prop.GetterAccess);
                if (prop.IsStatic) psb.Append("static ");
                psb.Append($"{prop.Type} {prop.Name} {{ ");
                if (prop.HasGet) psb.Append("get; ");
                if (prop.HasSet)
                {
                    if (prop.SetterAccess != prop.GetterAccess)
                        psb.Append($"{prop.SetterAccess.TrimEnd()} set; ");
                    else
                        psb.Append("set; ");
                }
                psb.Append('}');
                if (defaults != null && defaults.TryGetValue(fieldName, out string? propDefault))
                    psb.Append($" = {propDefault};");
                sb.AppendLine(psb.ToString());
                emittedAny = true;
                continue;
            }

            string fieldType = TypeUtils.CleanTypeName(context.TypeResolver.ResolveTypeName(fd.TypeIndex));

            var fsb = new StringBuilder();
            fsb.Append(fieldIndent);
            fsb.Append(ClassHeaderBuilder.GetFieldAccessStr(fd));

            if (fd.IsLiteral)
                fsb.Append("const ");
            else
            {
                if (fd.IsStatic) fsb.Append("static ");
                if (fd.IsReadOnly) fsb.Append("readonly ");
            }

            fsb.Append($"{fieldType} {fieldName}");

            if (fd.IsLiteral && defaultLookup.TryGetValue(fieldIdx, out var fdv))
            {
                Rosetta.Common.Il2CppTypeEnum fieldTypeEnum = Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I4;
                if (fd.TypeIndex >= 0)
                {
                    var ti = context.TypeResolver.GetTypeByIndex(fd.TypeIndex);
                    if (ti.HasValue) fieldTypeEnum = ti.Value.TypeEnum;
                }
                string? constVal = ReadEnumConstant(fdv, context, fieldTypeEnum);
                if (constVal != null)
                {
                    if (fieldType == "string")
                        fsb.Append($" = \"{constVal}\"");
                    else
                        fsb.Append($" = {constVal}");
                }
            }
            else if (defaults != null && defaults.TryGetValue(fieldName, out string? defVal))
            {
                if (fieldName == "nativePointer") Console.WriteLine($"DEBUG: nativePointer fieldType='{fieldType}', defVal='{defVal}'");
                if (defVal.Trim() == "null" && (fieldType == "System.IntPtr" || fieldType == "System.UIntPtr" || fieldType == "IntPtr" || fieldType == "UIntPtr"))
                    defVal = fieldType + ".Zero";
                fsb.Append($" = {defVal}");
            }
            fsb.Append(';');
            sb.AppendLine(fsb.ToString());
            emittedAny = true;
        }

        if (emittedAny)
            sb.AppendLine();
    }

    // ─── Enum Emission ──────────────────────────────────────────────────────

    /// <summary>Emit enum member names with values from metadata.</summary>
    public static void EmitEnumMembers(StringBuilder sb, TypeDefinition td,
        Il2CppContext context, string indent)
    {
        if (context.Metadata == null) return;

        string memberIndent = indent + "    ";

        var defaultLookup = new Dictionary<int, FieldDefaultValueDef>();
        foreach (var fdv in context.Metadata.FieldDefaultValues)
            defaultLookup[fdv.FieldIndex] = fdv;

        var members = new List<(string name, string? value)>();
        var enumFieldDefaults = new List<(string name, FieldDefaultValueDef fdv)>();

        for (int f = 0; f < td.FieldCount; f++)
        {
            int fieldIdx = td.FieldStart + f;
            if (fieldIdx < 0 || fieldIdx >= context.Metadata.FieldDefinitions.Length) continue;

            var fd = context.Metadata.FieldDefinitions[fieldIdx];
            string fieldName = fd.Name ?? $"Field_{f}";
            if (fieldName == "value__") continue;

            if (defaultLookup.TryGetValue(fieldIdx, out var fdv))
                enumFieldDefaults.Add((fieldName, fdv));
            else
                members.Add((fieldName, null));
        }

        foreach (var (name, fdv) in enumFieldDefaults)
        {
            Rosetta.Common.Il2CppTypeEnum underlyingType = Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I4;
            if (fdv.TypeIndex >= 0)
            {
                var typeInfo = context.TypeResolver?.GetTypeByIndex(fdv.TypeIndex);
                if (typeInfo.HasValue)
                {
                    underlyingType = typeInfo.Value.TypeEnum;
                }
            }
            
            string? valueStr = ReadEnumConstant(fdv, context, underlyingType);
            members.Add((name, valueStr));
        }

        for (int i = 0; i < members.Count; i++)
        {
            string comma = i < members.Count - 1 ? "," : "";
            string entry = members[i].value != null
                ? $"{memberIndent}{members[i].name} = {members[i].value}{comma}"
                : $"{memberIndent}{members[i].name}{comma}";
            sb.AppendLine(entry);
        }
    }

    private static string? ReadEnumConstant(FieldDefaultValueDef fdv, Il2CppContext context, Rosetta.Common.Il2CppTypeEnum underlyingType)
    {
        if (context.Metadata == null) return null;
        if (fdv.DataIndex < 0) return null;

        var blob = context.Metadata.DefaultValuesData;
        if (fdv.DataIndex >= blob.Length) return null;

        int offset = fdv.DataIndex;
        bool isSigned = underlyingType switch
        {
            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I1 => true,
            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I2 => true,
            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I4 => true,
            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I8 => true,
            _ => false
        };

        if (MetadataDecoder.TryReadCompressedUInt32(blob, ref offset, out uint raw))
        {
            long value = MetadataDecoder.DecodeZigZag(raw, isSigned);
            return value.ToString();
        }
        return null;
    }

    // ─── Non-Auto Property Emission ─────────────────────────────────────────

    /// <summary>Emit non-auto properties with full getter/setter bodies.</summary>
    public static void EmitNonAutoProperties(StringBuilder sb, PropertyMap propInfo,List<MethodAnalysisResult> methods, CSharpEmitter emitter, string indent)
    {
        if (propInfo.NonAutoProperties.Count == 0) return;

        string propIndent = indent + "    ";
        string bodyIndent = propIndent + "    ";

        var methodByName = new Dictionary<string, AstMethod>();
        foreach (var m in methods)
        {
            if (m.Ast != null && m.Ast.MethodName != null)
                methodByName[m.Ast.MethodName] = m.Ast;
        }

        foreach (var prop in propInfo.NonAutoProperties)
        {
            AstMethod? getterAst = prop.GetterMethodName != null && methodByName.TryGetValue(prop.GetterMethodName, out var g) ? g : null;
            AstMethod? setterAst = prop.SetterMethodName != null && methodByName.TryGetValue(prop.SetterMethodName, out var s) ? s : null;

            // Expression-bodied: getter-only with single return
            if (prop.HasGet && !prop.HasSet && getterAst != null)
            {
                string? exprBody = TryGetExpressionBody(getterAst, emitter);
                if (exprBody != null)
                {
                    sb.AppendLine($"{propIndent}{prop.GetterAccess}{(prop.IsStatic ? "static " : "")}{prop.Type} {prop.Name} => {exprBody};");
                    sb.AppendLine();
                    continue;
                }
            }

            // Full property declaration
            sb.AppendLine($"{propIndent}{prop.GetterAccess}{(prop.IsStatic ? "static " : "")}{prop.Type} {prop.Name}");
            sb.AppendLine($"{propIndent}{{");

            if (prop.HasGet && getterAst != null)
            {
                string getBody = EmitPropertyAccessorBody(getterAst, emitter);
                if (IsSingleLineBody(getBody))
                    sb.AppendLine($"{bodyIndent}get {{ {getBody.Trim()} }}");
                else
                {
                    sb.AppendLine($"{bodyIndent}get");
                    sb.AppendLine($"{bodyIndent}{{");
                    foreach (string line in getBody.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            sb.AppendLine($"{bodyIndent}    {line.TrimEnd()}");
                    }
                    sb.AppendLine($"{bodyIndent}}}");
                }
            }

            if (prop.HasSet && setterAst != null)
            {
                string setBody = EmitPropertyAccessorBody(setterAst, emitter);
                string setPrefix = prop.SetterAccess != prop.GetterAccess
                    ? $"{prop.SetterAccess.TrimEnd()} " : "";
                if (IsSingleLineBody(setBody))
                    sb.AppendLine($"{bodyIndent}{setPrefix}set {{ {setBody.Trim()} }}");
                else
                {
                    sb.AppendLine($"{bodyIndent}{setPrefix}set");
                    sb.AppendLine($"{bodyIndent}{{");
                    foreach (string line in setBody.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            sb.AppendLine($"{bodyIndent}    {line.TrimEnd()}");
                    }
                    sb.AppendLine($"{bodyIndent}}}");
                }
            }

            sb.AppendLine($"{propIndent}}}");
            sb.AppendLine();
        }
    }

    // ─── Field Defaults Extraction ──────────────────────────────────────────

    /// <summary>Extract field default values from .ctor and .cctor methods.</summary>
    public static Dictionary<string, string> ExtractFieldDefaults(List<MethodAnalysisResult> methods, string typeName, CSharpEmitter emitter)
    {
        var defaults = new Dictionary<string, string>();

        string shortType = typeName;
        int lastDot = typeName.LastIndexOf('.');
        if (lastDot >= 0) shortType = typeName[(lastDot + 1)..];

        foreach (var result in methods)
        {
            if (result.Ast == null) continue;
            if (result.Ast.MethodName != ".ctor" && result.Ast.MethodName != ".cctor") continue;

            var stmtsToRemove = new List<AstNode>();

            foreach (var stmt in result.Ast.Body.Statements)
            {
                if (stmt is not AstExpressionStatement es) continue;

                string? fieldName = null;
                string? valueStr = null;

                if (es.Expression is AstAssignment assign && assign.Target is AstMemberAccess ma)
                {
                    string targetStr = emitter.EmitExprPublic(ma.Target);
                    if (targetStr == "this" || targetStr == shortType || targetStr == typeName)
                    {
                        fieldName = ma.MemberName;
                        valueStr = emitter.EmitExprPublic(assign.Value);
                    }
                }
                else if (es.Expression is AstIdentifier id)
                {
                    string name = id.Name;
                    string? prefix = null;
                    if (name.StartsWith("this."))
                        prefix = "this.";
                    else if (name.StartsWith(shortType + "."))
                        prefix = shortType + ".";
                    else if (name.StartsWith(typeName + "."))
                        prefix = typeName + ".";

                    string rest = prefix != null ? name[prefix.Length..] : name;

                    int eqIdx = rest.IndexOf(" = ");
                    if (eqIdx > 0)
                    {
                        fieldName = rest[..eqIdx];
                        valueStr = rest[(eqIdx + 3)..];
                        if (prefix == null)
                            fieldName = $"<{fieldName}>k__BackingField";
                    }
                }

                if (fieldName != null && valueStr != null)
                {
                    defaults[fieldName] = valueStr;
                    stmtsToRemove.Add(stmt);
                }
            }

            if(!Il2cppConfig.DisableFieldRemoveCTR)
                foreach (var stmt in stmtsToRemove)
                    result.Ast.Body.Statements.Remove(stmt);
        }

        return defaults;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string? TryGetExpressionBody(AstMethod ast, CSharpEmitter emitter)
    {
        var meaningfulStmts = ast.Body.Statements
            .Where(s => s is not AstIfStatement)
            .ToList();

        if (meaningfulStmts.Count == 1 && meaningfulStmts[0] is AstReturnStatement ret && ret.Value != null)
            return emitter.EmitExprPublic(ret.Value);

        var returns = ast.Body.Statements.OfType<AstReturnStatement>().ToList();
        if (returns.Count == 1 && returns[0].Value != null)
        {
            var nonIfNonReturn = ast.Body.Statements
                .Where(s => s is not AstIfStatement && s is not AstReturnStatement)
                .ToList();
            if (nonIfNonReturn.Count == 0)
                return emitter.EmitExprPublic(returns[0].Value!);
        }

        return null;
    }

    private static string EmitPropertyAccessorBody(AstMethod ast, CSharpEmitter emitter)
    {
        var sb = new StringBuilder();
        foreach (var stmt in ast.Body.Statements)
        {
            if (stmt is AstIfStatement ifStmt)
            {
                string cond = emitter.EmitExprPublic(ifStmt.Condition);
                if (cond.Contains("metadata_var")) continue;
            }
            if (stmt is AstReturnStatement ret && ret.Value == null) continue;

            string code = emitter.Emit(new AstMethod
            {
                MethodName = "_accessor_",
                Body = new AstBlock { Statements = { stmt } }
            });
            foreach (string line in code.Split('\n'))
            {
                string trimmed = line.TrimEnd();
                if (trimmed.StartsWith("void _accessor_") || trimmed == "{" || trimmed == "}") continue;
                if (!string.IsNullOrWhiteSpace(trimmed))
                    sb.AppendLine(trimmed.TrimStart());
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static bool IsSingleLineBody(string body)
    {
        var lines = body.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        return lines.Length == 1;
    }
}
