using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.Resolve;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;
using Rosetta.Analysis.Utils;

namespace Rosetta.Analysis.AST;

/// <summary>Call expression builder: new, typeof, box, .ctor, managed/static calls.</summary>
public sealed partial class ExprPropagator
{
    private ExprNode? BuildCall(IrInstruction inst)
    {
        string? annotation = inst.Annotation;
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      BuildCall: ann=\"{annotation ?? "null"}\" sources={inst.Sources.Length}");

        if (annotation == null)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → unknown_call (no annotation)");
            return new ExprCall("unknown_call");
        }

        int? methodIndex = null;
        
        // Handle [M:...] prefix which might be at the start or after "new "
        int mIndexStart = annotation.IndexOf("[M:");
        if (mIndexStart >= 0)
        {
            int closeBracket = annotation.IndexOf(']', mIndexStart);
            if (closeBracket > mIndexStart + 3 && int.TryParse(annotation.Substring(mIndexStart + 3, closeBracket - mIndexStart - 3), out int parsedIdx))
            {
                methodIndex = parsedIdx;
                // Remove the [M:...] part from the annotation
                annotation = annotation.Remove(mIndexStart, closeBracket - mIndexStart + 1).TrimStart();
                if (mIndexStart > 0 && annotation[mIndexStart - 1] == ' ')
                    annotation = annotation.Remove(mIndexStart - 1, 1);
            }
        }

        // Suppress internals or intercept memcpy
        if (TryInterceptRuntimeInternalsAndMemcpy(annotation, inst, out var interceptRes))
            return interceptRes;

        // Handle array creations
        var arrayExpr = TryHandleArrayCreation(annotation);
        if (arrayExpr != null) return arrayExpr;
        if (annotation.StartsWith("new ") && annotation.EndsWith("()"))
        {
            var newTypeName = Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(annotation[4..^2], _ctx.Usings);
            var boxedValue = BoxingDetector.TryDetectPrimitiveBox(newTypeName, _ctx);
            if (boxedValue != null)
                return boxedValue;

            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → new object: {newTypeName}");
            return new ExprNew(newTypeName);
        }
        if (annotation.StartsWith("typeof("))
        {
            var typeofName = Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(annotation[7..^1], _ctx.Usings);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → typeof({typeofName})");
            return new ExprTypeOf(typeofName);
        }

        // Handle struct_box_local — unfused cross-block cases
        if (annotation == "struct_box_local")
            return ResolveStructBoxLocal(inst);

        // Handle is<T> / cast<T>
        var typeCheckExpr = TryHandleTypeChecks(annotation, inst);
        if (typeCheckExpr != null) return typeCheckExpr;

        // Handle box<T> — value type boxing
        if (annotation.StartsWith("box<") && annotation.EndsWith(">"))
        {
            return BoxingDetector.TryDetectValueTypeBox(annotation, inst, _ssa, ExprMap, _ctx, GetSourceExpr);
        }

        // InitializeArray array literal recovery
        if (annotation.Contains("InitializeArray") && annotation.Contains("RuntimeHelpers"))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → InitializeArray detected, attempting recovery");
            if (ArrayLiteralRecovery.TryRecover(inst, _ssa, ExprMap, _ctx, _fieldRvaResolver))
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → InitializeArray recovered as array literal");
                return null; // suppress the call
            }
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → InitializeArray recovery failed, emitting as call");
        }

        var parsed = AnnotationParser.Parse(annotation);
        annotation = parsed.FullAnnotation;
        bool isStatic = parsed.IsStatic;
        string methodName = parsed.MethodName ?? parsed.FullAnnotation;
        string? typeName = parsed.TypeName;

        if (!isStatic) isStatic = IsStaticMethod(annotation);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        call: {typeName ?? "?"}.{methodName} static={isStatic}");

        int firstArgSource = isStatic ? 1 : 2;
        int lastArgSource = inst.Sources.Length - 1;

        // Detect trailing MethodInfo*
        string? genericTypeArgs = null;
        if (lastArgSource >= firstArgSource)
        {
            bool isManagedCall = annotation != null && annotation.Contains("::");
            if (isManagedCall)
            {
                // Extract generic type from MethodRef before stripping
                genericTypeArgs = TryExtractGenericArgs(inst, lastArgSource);
                lastArgSource--;
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          strip MethodInfo* (managed call){(genericTypeArgs != null ? $" generic={genericTypeArgs}" : "")}");
            }
            else
            {
                var lastSrc = inst.Sources[lastArgSource];
                if (lastSrc.Kind == IrOperandKind.Register)
                {
                    var lastSsaVar = _ssa.GetSource(inst.Address, lastArgSource);
                    if (lastSsaVar.HasValue &&
                        ExprMap.TryGetValue(lastSsaVar.Value, out var lastExpr) &&
                        IsNullOrZeroLiteral(lastExpr))
                    {
                        lastArgSource--;
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          strip MethodInfo* (SSA null check)");
                    }
                }
            }
        }

        // Resolve target (receiver)
        ExprNode? target = ResolveCallTarget(annotation, inst, typeName, isStatic);

        IReadOnlyList<Rosetta.Model.MethodSignature.ParamEntry>? parameters = null;
        if (methodIndex.HasValue)
            parameters = _typeModel?.Signatures.GetValueOrDefault(methodIndex.Value)?.Parameters;
        else
            parameters = _typeModel?.GetMethodParameters(annotation!);

        // Collect arguments
        var args = CollectCallArguments(inst, firstArgSource, lastArgSource, parameters);

        // Reorder and decorate arguments (AAPCS, out/ref, HFA grouping, coercions)
        ReorderAndDecorateArguments(args, inst, firstArgSource, methodName, parameters);

        // .ctor handling
        if (annotation != null && (annotation.EndsWith(".ctor") || annotation.Contains("::.ctor")))
        {
            return ConstructorResolver.TryResolve(inst, annotation, _ssa, ExprMap, _ctx,
                Resolve, args, IsThisExpr, IsStackPointerExpr, IsLocalSpVar, GetSpOffset);
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → {target?.Emit() ?? "?"}.{methodName}({string.Join(", ", args.Select(a => a.Emit()))})");

        // Desugar C# operator methods to binary expressions
        string? opSymbol = Rosetta.Analysis.Utils.OpUtils.MethodNameToOperator(methodName);
        if (opSymbol != null && args.Count == 2)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → desugar operator: {args[0].Emit()} {opSymbol} {args[1].Emit()}");
            return new ExprBinary(opSymbol, args[0], args[1]);
        }
        if (methodName == "op_Implicit" && args.Count == 1)
        {
            return args[0];
        }
        if ((methodName is "il2cpp_type_get_object" or "il2cpp_codegen_type_get_object" ||
             methodName is "il2cpp_codegen_string_new_wrapper" or "il2cpp_string_new_wrapper") && args.Count > 0)
        {
            return args[0];
        }
        if (methodName is "il2cpp_object_unbox" or "il2cpp_codegen_object_unbox" && args.Count > 0)
        {
            return new ExprCall("Unbox", null, args);
        }

        // Append generic type parameters if extracted from MethodRef
        if (genericTypeArgs != null && !methodName.Contains('<'))
            methodName += genericTypeArgs;

        ExprNode callExpr = new ExprCall(Rosetta.Analysis.Utils.StringUtils.CleanMethodName(methodName), target, args);
        ApplyEnumMetadataCallCoercions((ExprCall)callExpr, typeName, methodName);

        // ── Universal Property Accessor Recovery ──
        if (methodIndex.HasValue && _typeModel != null)
        {
            string? propName = _typeModel.GetPropertyNameFromAccessor(methodIndex.Value, out bool isGetter);
            if (propName != null)
            {
                if (isGetter)
                {
                    if (args.Count == 0)
                    {
                        callExpr = new ExprField(target ?? new ExprVar("?"), propName, isProperty: true);
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          property get → {callExpr.Emit()}");
                    }
                    else if (args.Count == 1 && (propName == "Item" || propName == "Chars"))
                    {
                        callExpr = new ExprIndex(target ?? new ExprVar("?"), args[0]);
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          indexer get → {callExpr.Emit()}");
                    }
                }
                else
                {
                    if (args.Count == 1)
                    {
                        var propField = new ExprField(target ?? new ExprVar("?"), propName, isProperty: true);
                        callExpr = new ExprAssign(propField, args[0]);
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          property set → {callExpr.Emit()}");
                    }
                    else if (args.Count == 2 && (propName == "Item" || propName == "Chars"))
                    {
                        var propIndex = new ExprIndex(target ?? new ExprVar("?"), args[0]);
                        callExpr = new ExprAssign(propIndex, args[1]);
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          indexer set → {callExpr.Emit()}");
                    }
                }
            }
        }

        // Try handle struct return & HFA returns
        var returnedStruct = TryHandleStructReturn(inst, callExpr, methodIndex, parameters);
        if (returnedStruct == null) return null;
        callExpr = returnedStruct;

        if (callExpr != null && callExpr.InferredType == null && methodIndex.HasValue && _typeModel != null && _typeModel.Signatures.TryGetValue(methodIndex.Value, out var sigRet))
        {
            if (sigRet.ReturnTypeName != "System.Void" && sigRet.ReturnTypeName != "void")
            {
                callExpr.InferredType = sigRet.ReturnTypeName;
            }
        }

        return callExpr;
    }

    private void ApplyEnumMetadataCallCoercions(ExprCall callExpr, string? typeName, string methodName)
    {
        if (_typeModel == null)
            return;

        if (typeName == "System.Type" &&
            methodName == "GetTypeFromHandle" &&
            callExpr.Args.Count > 0 &&
            callExpr.Args[0].MetadataTypeDefIndex >= 0)
        {
            callExpr.MetadataTypeDefIndex = callExpr.Args[0].MetadataTypeDefIndex;
            return;
        }

        if (typeName == "System.Enum" &&
            methodName == "IsDefined" &&
            callExpr.Args.Count >= 2 &&
            callExpr.Args[0].MetadataTypeDefIndex >= 0 &&
            TryGetIntegralLiteral(callExpr.Args[1], out long rawValue))
        {
            string? enumLiteral = _typeModel.ResolveEnumLiteralByTypeDefIndex(
                callExpr.Args[0].MetadataTypeDefIndex,
                rawValue);
            if (enumLiteral != null)
                callExpr.Args[1] = new ExprVar(enumLiteral);
        }
    }

    private static bool TryGetIntegralLiteral(ExprNode value, out long rawValue)
    {
        rawValue = 0;
        if (value is not ExprLiteral lit) return false;

        switch (lit.Value)
        {
            case int i:
                rawValue = i;
                return true;
            case long l:
                rawValue = l;
                return true;
            case uint ui:
                rawValue = ui;
                return true;
            case ulong ul when ul <= long.MaxValue:
                rawValue = (long)ul;
                return true;
            case byte b:
                rawValue = b;
                return true;
            case sbyte sb:
                rawValue = sb;
                return true;
            case short s:
                rawValue = s;
                return true;
            case ushort us:
                rawValue = us;
                return true;
            default:
                return false;
        }
    }
}
