using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Analysis.AST;
using Rosetta.Binary;
using Rosetta.CodeGen;
using Rosetta.Common;
using Rosetta.Metadata;
using Rosetta.Model;

namespace Rosetta.Pipeline.Stages;

/// <summary>
/// Pipeline stage that emits C# scripts from completed AssemblyAssets.
/// Each assembly gets its own output directory. Each top-level type gets one .cs file.
/// Nested types are emitted inside their parent class body.
///
/// Consumes: context.AssemblyAssets (populated by AssemblyPipelineStage)
/// Produces: .cs files on disk
///
/// Delegates to focused helper classes:
///   - ClassHeaderBuilder: class/struct/interface/enum headers
///   - MemberEmitter: fields, properties, enum members
///   - CSharpEmitter: method body emission
/// </summary>
public class CodeGenStage : IPipelineStage
{
    public string Name => "CODEGEN: Emitting C# scripts";

    public void Execute(PipelineContext context)
    {
        if (context.AssemblyAssets.Count == 0)
            throw new InvalidOperationException("No assembly assets to emit — run AssemblyPipelineStage first");

        var config = context.Config;
        string outputDir = config.OutputDirectory;
        ConsoleReporter.SetCategory("Export");

        int totalEmittedMethods = 0;
        int totalEmittedFiles = 0;

        // Build type name → TypeDefinition lookup (needed by ClassHeaderBuilder)
        var typeNameToTd = new Dictionary<string, TypeDefinition>();
        if (context.Metadata != null)
        {
            for (int t = 0; t < context.Metadata.TypeDefinitions.Length; t++)
            {
                var td = context.Metadata.TypeDefinitions[t];
                typeNameToTd[td.FullName] = td;
            }
        }

        // ── Process each assembly ───────────────────────────────────────────
        foreach (var assembly in context.AssemblyAssets)
        {
            string asmDir = Path.Combine(outputDir, Sanitize(assembly.Name));
            if (Directory.Exists(asmDir))
                Directory.Delete(asmDir, true);
            Directory.CreateDirectory(asmDir);

            int localEmittedMethods = 0;
            int localEmittedFiles = 0;

            System.Threading.Tasks.Parallel.ForEach(
                assembly.Scripts,
                () => (Emitter: new CSharpEmitter(), Methods: 0, Files: 0),
                (script, loopState, localState) =>
                {
                    int m = 0;
                    int f = 0;
                    EmitScript(script, asmDir, context.Il2Cpp!, typeNameToTd, localState.Emitter, ref m, ref f);
                    return (
                        localState.Emitter,
                        localState.Methods + m,
                        localState.Files + f
                    );
                },
                localState =>
                {
                    System.Threading.Interlocked.Add(ref localEmittedMethods, localState.Methods);
                    System.Threading.Interlocked.Add(ref localEmittedFiles, localState.Files);
                }
            );

            assembly.EmittedFiles = localEmittedFiles;
            totalEmittedMethods += localEmittedMethods;
            totalEmittedFiles += localEmittedFiles;

            // per-assembly stats kept silent — total printed at end
        }

        ConsoleReporter.Log("Export", $"Scripts: {totalEmittedMethods:N0} methods → {totalEmittedFiles:N0} .cs files");
    }

    // ─── Per-Script Emission ────────────────────────────────────────────────

    private void EmitScript( ScriptAsset script, string asmDir, Il2CppContext context, Dictionary<string, TypeDefinition> typeNameToTd, CSharpEmitter emitter, ref int emittedMethods, ref int emittedFiles)
    {
        var sb = new StringBuilder();
        string ns = script.Namespace;
        string shortName = script.Name;

        sb.AppendLine("// ═══════════════════════════════════════════════════════════");
        sb.AppendLine($"// Decompiled by Rosetta — {script.FullName}");
        sb.AppendLine("// ═══════════════════════════════════════════════════════════");
        sb.AppendLine();

        /// Add Usings to header of class
        foreach (var u in script.Usings)
        {
            if(u == ns)
                continue;

            string @using = $"using {u};";
            sb.AppendLine(@using);
        }
        sb.AppendLine();

        if (!string.IsNullOrEmpty(ns))
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
        }

        string classIndent = string.IsNullOrEmpty(ns) ? "" : "    ";

        // Type-level custom attributes (e.g. [Serializable], [RequireComponent])
        EmitAttributes(sb, script.Attributes, classIndent);

        var propInfo = BuildPropertyMapFromScript(script);
        if (IsEmpty(script, propInfo))
        {
            string classHeader = ClassHeaderBuilder.Build(script.FullName, shortName, typeNameToTd, context);
            sb.AppendLine($"{classIndent}{classHeader} {{ }}");
        }
        else
        {
            // Class header
            string classHeader = ClassHeaderBuilder.Build(script.FullName, shortName, typeNameToTd, context);
            sb.AppendLine($"{classIndent}{classHeader}");
            sb.AppendLine($"{classIndent}{{");

            if (script.IsEnum)
            {
                // Enum members
                MemberEmitter.EmitEnumMembers(sb, script.TypeDef, context, classIndent);
            }
            else
            {
                var fieldDefaults = ExtractFieldDefaultsFromScript(script, emitter);
                var blocks = new List<string>();

                // Nested types
                if (script.NestedTypes.Count > 0)
                {
                    var nestedBlocks = new List<string>();
                    foreach (var nested in script.NestedTypes)
                    {
                        var nestedSb = new StringBuilder();
                        EmitNestedScript(nestedSb, nested, script.FullName, context, typeNameToTd,
                            emitter, classIndent, ref emittedMethods);
                        string content = nestedSb.ToString().TrimEnd();
                        if (!string.IsNullOrEmpty(content))
                        {
                            nestedBlocks.Add(content);
                        }
                    }
                    if (nestedBlocks.Count > 0)
                    {
                        blocks.Add(string.Join("\n\n", nestedBlocks));
                    }
                }

                // Fields and auto-properties
                var fieldsSb = new StringBuilder();
                EmitFieldsFromScript(fieldsSb, script, classIndent, fieldDefaults, propInfo);
                string fieldsContent = fieldsSb.ToString().TrimEnd();
                if (!string.IsNullOrEmpty(fieldsContent))
                {
                    blocks.Add(fieldsContent);
                }

                // Non-auto properties
                var propsSb = new StringBuilder();
                EmitNonAutoPropertiesFromScript(propsSb, script, emitter, classIndent, propInfo);
                string propsContent = propsSb.ToString().TrimEnd();
                if (!string.IsNullOrEmpty(propsContent))
                {
                    blocks.Add(propsContent);
                }

                // Methods
                var methodsSb = new StringBuilder();
                EmitMethodsFromScript(methodsSb, script, emitter, classIndent, ref emittedMethods);
                string methodsContent = methodsSb.ToString().TrimEnd();
                if (!string.IsNullOrEmpty(methodsContent))
                {
                    blocks.Add(methodsContent);
                }

                if (blocks.Count > 0)
                {
                    sb.AppendLine(string.Join("\n\n", blocks));
                }
            }

            sb.AppendLine($"{classIndent}}}");
        }

        if (!string.IsNullOrEmpty(ns))
            sb.AppendLine("}");

        // Write file
        string fileDir = asmDir;
        if (!string.IsNullOrEmpty(ns))
        {
            string[] nsParts = ns.Split('.');
            foreach (string part in nsParts)
                fileDir = Path.Combine(fileDir, Sanitize(part));
            Directory.CreateDirectory(fileDir);
        }

        string filePath = Path.Combine(fileDir, Sanitize(shortName) + ".cs");
        File.WriteAllText(filePath, sb.ToString());
        emittedFiles++;
    }

    // ─── Nested Script Emission ─────────────────────────────────────────────

    private void EmitNestedScript(
        StringBuilder sb,
        ScriptAsset nested,
        string parentFullName,
        Il2CppContext context,
        Dictionary<string, TypeDefinition> typeNameToTd,
        CSharpEmitter emitter,
        string parentIndent,
        ref int emittedMethods)
    {
        string nestedIndent = parentIndent + "    ";
        string nestedShort = nested.Name;

        // Nested type attributes
        var attrSb = new StringBuilder();
        EmitAttributes(attrSb, nested.Attributes, nestedIndent);
        string attrStr = attrSb.ToString();
        if (!string.IsNullOrEmpty(attrStr))
        {
            sb.Append(attrStr);
        }

        var nestedPropInfo = BuildPropertyMapFromScript(nested);
        if (IsEmpty(nested, nestedPropInfo))
        {
            string header = ClassHeaderBuilder.Build(nested.FullName, nestedShort, typeNameToTd, context);
            sb.AppendLine($"{nestedIndent}{header} {{ }}");
            return;
        }

        string headerNormal = ClassHeaderBuilder.Build(nested.FullName, nestedShort, typeNameToTd, context);
        sb.AppendLine($"{nestedIndent}{headerNormal}");
        sb.AppendLine($"{nestedIndent}{{");

        if (nested.IsEnum)
        {
            MemberEmitter.EmitEnumMembers(sb, nested.TypeDef, context, nestedIndent);
        }
        else
        {
            var nestedDefaults = ExtractFieldDefaultsFromScript(nested, emitter);
            var blocks = new List<string>();

            // Recursively emit sub-nested types
            if (nested.NestedTypes.Count > 0)
            {
                var subNestedBlocks = new List<string>();
                foreach (var subNested in nested.NestedTypes)
                {
                    var subNestedSb = new StringBuilder();
                    EmitNestedScript(subNestedSb, subNested, nested.FullName, context, typeNameToTd,
                        emitter, nestedIndent, ref emittedMethods);
                    string content = subNestedSb.ToString().TrimEnd();
                    if (!string.IsNullOrEmpty(content))
                    {
                        subNestedBlocks.Add(content);
                    }
                }
                if (subNestedBlocks.Count > 0)
                {
                    blocks.Add(string.Join("\n\n", subNestedBlocks));
                }
            }

            // Fields
            var fieldsSb = new StringBuilder();
            EmitFieldsFromScript(fieldsSb, nested, nestedIndent, nestedDefaults, nestedPropInfo);
            string fieldsContent = fieldsSb.ToString().TrimEnd();
            if (!string.IsNullOrEmpty(fieldsContent))
            {
                blocks.Add(fieldsContent);
            }

            // Non-auto properties
            var propsSb = new StringBuilder();
            EmitNonAutoPropertiesFromScript(propsSb, nested, emitter, nestedIndent, nestedPropInfo);
            string propsContent = propsSb.ToString().TrimEnd();
            if (!string.IsNullOrEmpty(propsContent))
            {
                blocks.Add(propsContent);
            }

            // Methods
            var methodsSb = new StringBuilder();
            EmitMethodsFromScript(methodsSb, nested, emitter, nestedIndent, ref emittedMethods);
            string methodsContent = methodsSb.ToString().TrimEnd();
            if (!string.IsNullOrEmpty(methodsContent))
            {
                blocks.Add(methodsContent);
            }

            if (blocks.Count > 0)
            {
                sb.AppendLine(string.Join("\n\n", blocks));
            }
        }

        sb.AppendLine($"{nestedIndent}}}");
    }

    // ─── Field Emission from ScriptAsset ────────────────────────────────────

    private void EmitFieldsFromScript(
        StringBuilder sb,
        ScriptAsset script,
        string indent,
        Dictionary<string, string> defaults,
        PropertyMapInfo propInfo)
    {
        if (script.Fields.Count == 0) return;

        string fieldIndent = indent + "    ";
        bool emittedAny = false;

        foreach (var field in script.Fields)
        {
            if (field.Name == "value__") continue;

            if (propInfo.AutoProperties.TryGetValue(field.Name, out var prop))
            {
                Rosetta.Analysis.Utils.AttrUtils.FormatAttributes(prop.Attributes, fieldIndent, out string standaloneLines, out string inlinePrefix, out bool wantsEmptyLine);
                if (wantsEmptyLine && emittedAny) sb.AppendLine();
                sb.Append(standaloneLines);

                var psb = new StringBuilder();
                psb.Append(fieldIndent);
                psb.Append(inlinePrefix);
                psb.Append(prop.GetterAccess);
                if (prop.IsStatic) psb.Append("static ");
                psb.Append($"{prop.TypeName} {prop.Name} {{ ");
                if (prop.HasGetter) psb.Append("get; ");
                if (prop.HasSetter)
                {
                    if (prop.SetterAccess != prop.GetterAccess)
                        psb.Append($"{prop.SetterAccess.TrimEnd()} set; ");
                    else
                        psb.Append("set; ");
                }
                psb.Append('}');
                if (defaults.TryGetValue(field.Name, out string? propDefault))
                    psb.Append($" = {propDefault};");
                sb.AppendLine(psb.ToString());
                emittedAny = true;
                continue;
            }

            Rosetta.Analysis.Utils.AttrUtils.FormatAttributes(field.Attributes, fieldIndent, out string fStandaloneLines, out string fInlinePrefix, out bool fWantsEmptyLine);
            if (fWantsEmptyLine && emittedAny) sb.AppendLine();
            sb.Append(fStandaloneLines);

            var fsb = new StringBuilder();
            fsb.Append(fieldIndent);
            fsb.Append(fInlinePrefix);
            fsb.Append(ClassHeaderBuilder.GetFieldAccessStr(field.FieldDef));

            if (field.IsLiteral)
                fsb.Append("const ");
            else
            {
                if (field.IsStatic) fsb.Append("static ");
                if (field.IsReadOnly) fsb.Append("readonly ");
            }

            fsb.Append($"{field.TypeName} {field.Name}");
            if (defaults.TryGetValue(field.Name, out string? defVal))
            {
                if (defVal.Trim() == "null" && (field.TypeName == "System.IntPtr" || field.TypeName == "System.UIntPtr" || field.TypeName == "IntPtr" || field.TypeName == "UIntPtr"))
                    defVal = field.TypeName + ".Zero";
                fsb.Append($" = {defVal}");
            }
            fsb.Append(';');
            sb.AppendLine(fsb.ToString());
            emittedAny = true;
        }
    }

    // ─── Non-Auto Property Emission from ScriptAsset ────────────────────────

    private void EmitNonAutoPropertiesFromScript(
        StringBuilder sb,
        ScriptAsset script,
        CSharpEmitter emitter,
        string indent,
        PropertyMapInfo propInfo)
    {
        if (propInfo.NonAutoProperties.Count == 0) return;

        string propIndent = indent + "    ";
        string bodyIndent = propIndent + "    ";

        // Build method name → AST lookup
        var methodByName = new Dictionary<string, AstMethod>();
        foreach (var m in script.Methods)
        {
            if (m.Ast != null && m.Ast.MethodName != null)
                methodByName[m.Ast.MethodName] = m.Ast;
        }

        foreach (var prop in propInfo.NonAutoProperties)
        {
            // Property-level attributes
            EmitAttributes(sb, prop.Attributes, propIndent);
            AstMethod? getterAst = prop.GetterMethodName != null && methodByName.TryGetValue(prop.GetterMethodName, out var g) ? g : null;
            AstMethod? setterAst = prop.SetterMethodName != null && methodByName.TryGetValue(prop.SetterMethodName, out var s) ? s : null;

            // Expression-bodied: getter-only with single return
            if (prop.HasGetter && !prop.HasSetter && getterAst != null)
            {
                string? exprBody = TryGetExpressionBody(getterAst, emitter);
                if (exprBody != null)
                {
                    sb.AppendLine($"{propIndent}{prop.GetterAccess}{(prop.IsStatic ? "static " : "")}{prop.TypeName} {prop.Name} => {exprBody};");
                    sb.AppendLine();
                    continue;
                }
            }

            // Full property
            sb.AppendLine($"{propIndent}{prop.GetterAccess}{(prop.IsStatic ? "static " : "")}{prop.TypeName} {prop.Name}");
            sb.AppendLine($"{propIndent}{{");

            if (prop.HasGetter && getterAst != null)
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

            if (prop.HasSetter && setterAst != null)
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

    // ─── Method Emission from ScriptAsset ───────────────────────────────────

    private void EmitMethodsFromScript(StringBuilder sb, ScriptAsset script, CSharpEmitter emitter, string classIndent, ref int emittedMethods)
    {
        bool emittedAny = false;
        foreach (var methodInfo in script.Methods)
        {
            // Skip property/event accessors
            if (script.SkipMethodNames.Contains(methodInfo.Name) ||  CSharpEmitter.IsConstructorEmpty(methodInfo))
            {
                emittedMethods++;
                continue;
            }

            if (emittedAny)
            {
                sb.AppendLine();
            }

            if (methodInfo.Ast != null)
            {
                // Emit method attributes before AST code
                string methodIndent = classIndent + "    ";
                EmitAttributes(sb, methodInfo.Attributes, methodIndent);

                // Emit from AST
                try
                {
                    string modifiers = GetMethodModifiers(methodInfo, script.IsInterface);
                    string code = emitter.Emit(methodInfo.Ast, modifiers).TrimEnd();
                    foreach (string line in code.Split('\n'))
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            sb.AppendLine($"{methodIndent}{line.TrimEnd()}");
                        else
                            sb.AppendLine();
                    }

                    methodInfo.CSharpCode = code;
                    emittedMethods++;
                    emittedAny = true;
                }
                catch
                {
                    sb.AppendLine($"{classIndent}    // Failed to emit: {methodInfo.Name}");
                }
            }
            else
            {
                // No AST — emit stub
                EmitMethodStub(sb, methodInfo, script, classIndent);
                emittedMethods++;
                emittedAny = true;
            }
        }
    }

    private static string GetMethodModifiers(ScriptAsset.MethodInfo method, bool isInterface)
    {
        if (isInterface)
            return "";

        var sb = new StringBuilder();
        ushort flags = method.Flags;

        // Access modifier
        sb.Append(ClassHeaderBuilder.GetMethodAccess(flags));

        // static
        if (method.IsStatic)
        {
            if (method.Name != ".cctor")
                sb.Append("static ");
        }
        else
        {
            // abstract / override / virtual
            if (method.IsAbstract)
            {
                sb.Append("abstract ");
            }
            else if (method.IsVirtual)
            {
                bool isNewSlot = (flags & 0x0100) != 0;
                if (!isNewSlot)
                    sb.Append("override ");
                else
                    sb.Append("virtual ");
            }
        }

        return sb.ToString();
    }

    // ─── Method Stub Emission ───────────────────────────────────────────────

    private void EmitMethodStub(StringBuilder sb, ScriptAsset.MethodInfo method, ScriptAsset script, string classIndent)
    {
        string enclosingTypeName = script.Name;
        string methodIndent = classIndent + "    ";
        string bodyIndent = methodIndent + "    ";

        // Method-level attributes (e.g. [Obsolete])
        EmitAttributes(sb, method.Attributes, methodIndent);

        string modifiers = GetMethodModifiers(method, script.IsInterface);

        // Clean name: strip backtick arity
        bool isCtor = method.Name == ".ctor" || method.Name == ".cctor";
        string cleanName = isCtor ? enclosingTypeName : method.Name;
        int backtick = cleanName.IndexOf('`');
        if (backtick >= 0) cleanName = cleanName[..backtick];

        // Parameters
        var paramStr = string.Join(", ", method.Parameters.Select(p => $"{p.TypeName} {p.Name}"));

        if (method.IsAbstract && !isCtor)
        {
            sb.AppendLine($"{methodIndent}{modifiers}{method.ReturnType} {cleanName}({paramStr});");
        }
        else
        {
            if (isCtor)
            {
                string access = ClassHeaderBuilder.GetMethodAccess(method.Flags);
                string staticMod = "";
                if (method.Name == ".cctor")
                {
                    access = ""; // Static constructors cannot have access modifiers
                    staticMod = "static ";
                }
                sb.AppendLine($"{methodIndent}{access}{staticMod}{cleanName}({paramStr})");
            }
            else
            {
                sb.AppendLine($"{methodIndent}{modifiers}{method.ReturnType} {cleanName}({paramStr})");
            }
            sb.AppendLine($"{methodIndent}{{");
            sb.AppendLine($"{bodyIndent}throw new System.NotImplementedException();");
            sb.AppendLine($"{methodIndent}}}");
        }
    }

    // ─── Attribute Emission Helper ─────────────────────────────────────

    /// <summary>
    /// Emit pre-formatted attribute strings (e.g. "[Serializable]") on separate
    /// lines above the declaration they annotate.
    /// </summary>
    private static void EmitAttributes(StringBuilder sb, List<string> attributes, string indent)
    {
        foreach (var attr in attributes)
            sb.AppendLine($"{indent}{attr}");
    }

    // ─── Property Map Builder ───────────────────────────────────────────────

    private sealed class PropertyMapInfo
    {
        /// <summary>Backing field name → auto-property.</summary>
        public Dictionary<string, ScriptAsset.PropertyInfo> AutoProperties { get; } = new();
        /// <summary>Non-auto properties.</summary>
        public List<ScriptAsset.PropertyInfo> NonAutoProperties { get; } = new();
    }

    private PropertyMapInfo BuildPropertyMapFromScript(ScriptAsset script)
    {
        var map = new PropertyMapInfo();
        foreach (var prop in script.Properties)
        {
            if (prop.IsAutoProperty && prop.BackingFieldName != null)
                map.AutoProperties[prop.BackingFieldName] = prop;
            else
                map.NonAutoProperties.Add(prop);
        }
        return map;
    }

    // ─── Field Defaults Extraction from ScriptAsset ─────────────────────────

    private Dictionary<string, string> ExtractFieldDefaultsFromScript(ScriptAsset script, CSharpEmitter emitter)
    {
        var defaults = new Dictionary<string, string>();
        string typeName = script.FullName;
        string shortType = script.Name;

        foreach (var method in script.Methods)
        {
            if (method.Ast == null) continue;
            if (script.IsStruct || script.IsValueType)
            {
                if (method.Ast.MethodName != ".cctor") continue;
            }
            else
            {
                if (method.Ast.MethodName != ".ctor" && method.Ast.MethodName != ".cctor") continue;
            }

            var stmtsToRemove = new List<AstNode>();
            
            // Get parameter names for this constructor to prevent extracting parameter-dependent defaults
            var paramNames = new HashSet<string>(method.Parameters.Select(p => p.Name), StringComparer.Ordinal);

            foreach (var stmt in method.Ast.Body.Statements)
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
                // this responsible for fields extraction later gone need to change it make it good not hardcoded
                else if (es.Expression is AstAssignment assign2 && assign2.Target is AstIdentifier targetId)
                {
                    string name = targetId.Name;
                    string? prefix = null;
                    if (name.StartsWith("this."))
                        prefix = "this.";
                    else if (name.StartsWith(shortType + "."))
                        prefix = shortType + ".";
                    else if (name.StartsWith(typeName + "."))
                        prefix = typeName + ".";

                    if (prefix != null)
                    {
                        fieldName = name[prefix.Length..];
                        valueStr = emitter.EmitExprPublic(assign2.Value);
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
                    // Check if valueStr references any constructor parameter using word boundaries
                    bool referencesParam = false;
                    foreach (var paramName in paramNames)
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(valueStr, @"\b" + System.Text.RegularExpressions.Regex.Escape(paramName) + @"\b"))
                        {
                            referencesParam = true;
                            break;
                        }
                    }

                    if (!referencesParam)
                    {
                        defaults[fieldName] = valueStr;
                        stmtsToRemove.Add(stmt);
                    }
                }
            }

            if (!Rosetta.Config.Il2cppConfig.DisableFieldRemoveCTR)
            {
                foreach (var stmt in stmtsToRemove)
                    method.Ast.Body.Statements.Remove(stmt);
            }
        }

        script.FieldDefaults.Clear();
        foreach (var (k, v) in defaults)
            script.FieldDefaults[k] = v;

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
            if (stmt is AstReturnStatement ret2 && ret2.Value == null) continue;

            string code = emitter.Emit(new AstMethod
            {
                MethodName = "_accessor_",
                Body = new AstBlock { Statements = { stmt } }
            });
            // The emitter renders the body at indent level 1 (4 spaces base).
            // Strip exactly that base indent so relative indentation for nested
            // structures (if-bodies, loops, etc.) is preserved.
            const string baseIndent = "    "; // 4 spaces — emitter indent level 1
            foreach (string line in code.Split('\n'))
            {
                string trimmed = line.TrimEnd();
                if (trimmed.StartsWith("void _accessor_") || trimmed == "{" || trimmed == "}") continue;
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    // Strip the base indent if present, preserving any extra indentation
                    string stripped = trimmed.StartsWith(baseIndent) ? trimmed[baseIndent.Length..] : trimmed.TrimStart();
                    sb.AppendLine(stripped);
                }
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static bool IsSingleLineBody(string body)
    {
        var lines = body.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        return lines.Length == 1;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }

    private static bool IsEmpty(ScriptAsset script, PropertyMapInfo propInfo)
    {
        if (script.IsEnum)
            return false;

        if (script.NestedTypes.Count > 0)
            return false;

        foreach (var field in script.Fields)
        {
            if (field.Name != "value__")
                return false;
        }

        if (propInfo.NonAutoProperties.Count > 0)
            return false;

        foreach (var method in script.Methods)
        {
            if (!script.SkipMethodNames.Contains(method.Name) && !CSharpEmitter.IsConstructorEmpty(method))
                return false;
        }

        return true;
    }
}
