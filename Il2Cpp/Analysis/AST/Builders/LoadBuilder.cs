using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

/// <summary>Load expression builders: BuildLoad, BuildLoadImm, BuildLoadAddr.</summary>
public sealed partial class ExprPropagator
{
    private ExprNode BuildLoad(IrInstruction inst)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] BuildLoad called! Opcode={inst.Opcode}");
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      BuildLoad: sources={inst.Sources.Length} ann=\"{inst.Annotation ?? "null"}\"");

        if (inst.Sources.Length == 0)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → ?load (no sources)");
            return new ExprLiteral("?load");
        }

        var src = inst.Sources[0];
        string? ann = inst.Annotation;

        if (inst.SemanticTag == IrSemanticTag.MethodRef && ann != null && ann.StartsWith("MethodRef(") && ann.EndsWith(")"))
        {
            string methodName = ann.Substring("MethodRef(".Length, ann.Length - "MethodRef(".Length - 1);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → MethodRef: {methodName}");
            return new ExprMethodRef(inst.TargetMethodIndex ?? -1, methodName);
        }

        if (inst.SemanticTag == IrSemanticTag.ClassInit)
        {
            // The annotation is currently just "class_init_flag" from GlobalAddressMap.
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → ClassInit: {ann}");
            return new ExprClassInit(ann ?? "unknown_class");
        }

        if (ann != null)
        {
            // sign-extend annotations (from LDRSW, LDRSB, LDRSH) are ARM64 load type
            // markers, NOT field names. Strip them and process the load as unannotated.
            // sign-extend 32→64 = int→long cast (implicit in C#)
            // sign-extend 8/16 = byte/short→int cast (implicit in C#)
            if (ann.StartsWith("sign-extend"))
            {
                ann = null;
                inst = new IrInstruction
                {
                    Address = inst.Address,
                    Opcode = inst.Opcode,
                    Destination = inst.Destination,
                    Sources = inst.Sources,
                    Annotation = null
                };
                // Fall through to unannotated memory load handling below
            }
        }

        if (ann != null)
        {
            if ((ann.StartsWith(".") || ann.StartsWith("->")) && src.Kind == IrOperandKind.Memory)
            {
                var baseExpr = GetRegExpr(src.Value, inst.Address, 0);
                string field = ann.StartsWith(".") ? ann[1..] : (ann.StartsWith("->") ? ann[2..] : ann);
                field = Rosetta.Analysis.Utils.StringUtils.CleanFieldName(field, _typeModel);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → field access: {baseExpr.Emit()}.{field}");
                return ApplyAnnotationTypeHint(new ExprField(baseExpr, field), ann, src);
            }

            var typeOrTypeOf = TryParseTypeOrTypeOf(ann, inst);
            if (typeOrTypeOf != null) return typeOrTypeOf;

            if (ann.StartsWith("[") && ann.EndsWith("]") && src.Kind == IrOperandKind.Memory)
            {
                var baseExpr = GetRegExpr(src.Value, inst.Address, 0);
                string indexStr = ann[1..^1];
                if (int.TryParse(indexStr, out int idx))
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → array index: {baseExpr.Emit()}[{idx}]");
                    return new ExprIndex(baseExpr, new ExprLiteral(idx));
                }
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → array index: {baseExpr.Emit()}[{indexStr}]");
                return new ExprIndex(baseExpr, new ExprVar(indexStr));
            }

            if (inst.Destination.HasValue &&
                inst.Destination.Value.Kind == IrOperandKind.FpRegister &&
                ann.Contains('.') && !ann.StartsWith("new ") &&
                !ann.StartsWith("\"") && !ann.Contains("static["))
            {
                var result = new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanFieldName(ann, _typeModel));
                result.StaticFieldHint = Rosetta.Analysis.Utils.StringUtils.CleanFieldName(ann, _typeModel);
                ApplyAnnotationTypeHint(result, ann, src);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → FP static field: {result.Emit()} (hint={result.StaticFieldHint})");
                return result;
            }

            // String literals: annotations like "\"Clicked at: \"" should be emitted as-is
            if (ann.StartsWith("\""))
            {
                // Strip surrounding quotes from the annotation
                string strVal = ann.Length >= 2 && ann.EndsWith("\"")
                    ? ann[1..^1]
                    : ann[1..];
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → string literal: \"{strVal}\"");
                return new ExprLiteral(strVal);
            }

            var fieldExpr = new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanFieldName(ann, _typeModel));
            ApplyAnnotationTypeHint(fieldExpr, ann, src);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → annotated load: {fieldExpr.Emit()} (raw=\"{ann}\")");
            return fieldExpr;
        }

        // Unannotated memory load
        if (src.Kind == IrOperandKind.Memory)
        {
            if (Rosetta.Analysis.Utils.ArmUtils.IsStackPointer(src.Value))
            {
                // Check if the SP slot was updated by a constructor call.
                // Constructor results are tracked in _stackSlotValues but
                // the SSA StackUseMap still maps to the stale zero-init.
                if (_ctx.StackSlotValues.TryGetValue(src.Offset, out var slotVal) &&
                    IsConstructorResult(slotVal))
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → SP load from ctor-updated slot 0x{src.Offset:X}: {slotVal.Emit()}");
                    return slotVal;
                }

                if (_ssa.StackUseMap.TryGetValue(inst.Address, out var stackUse))
                {
                    var resolved = Resolve(stackUse);
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → SP load via SSA stack-use: {stackUse.Name} v{stackUse.Version} → {resolved.Emit()}");
                    return resolved;
                }

                if (slotVal != null)
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → SP load fallback 0x{src.Offset:X}: {slotVal.Emit()}");
                    return slotVal;
                }

                var stackField = TryResolveStackStructField(src.Offset);
                if (stackField != null)
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → SP field load 0x{src.Offset:X}: {stackField.Emit()}");
                    return stackField;
                }
            }

            var baseExpr = GetRegExpr(src.Value, inst.Address, 0);
            if (baseExpr is ExprTypeOf typeOfExpr && src.Offset == Rosetta.Common.Constants.ClassInitFlagOffset)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → dynamically resolved class_init_flag for {typeOfExpr.TypeName}");
                return new ExprClassInit(typeOfExpr.TypeName);
            }

            if (src.Value == 31 || src.Value == 13) // ARM64 SP=31, ARM32 SP=13
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → SP load fallback: local_sp{src.Offset:X}");
                return new ExprSpSlot(src.Offset);
            }

            // Frame-pointer-relative load: x29 = SP + fpOffset, so [x29 + off] = [SP + fpOffset + off]
            if (src.Value == 29 && _ctx.FpSpOffset >= 0)
            {
                long effectiveSpOffset = _ctx.FpSpOffset + src.Offset;
                if (_ctx.StackSlotValues.TryGetValue(effectiveSpOffset, out var fpSlotVal))
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → FP load from slot SP+0x{effectiveSpOffset:X}: {fpSlotVal.Emit()}");
                    return fpSlotVal;
                }
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → FP load fallback: local_sp{effectiveSpOffset:X}");
                return new ExprSpSlot(effectiveSpOffset);
            }


            if (src.Offset == 0)
            {
                // Check for register-indexed load (e.g., LDR St, [Xn, Xm])
                // These have 2 sources: Sources[0]=memory base, Sources[1]=index register
                if (inst.Sources.Length > 1 && inst.Sources[1].Kind == IrOperandKind.Register)
                {
                    // The index register typically holds a byte offset computed from
                    // arr.Length via compiler arithmetic (e.g., (length-1)*4 for float[]).
                    // Try to trace back to the .Length usage and simplify.
                    var indexExpr = ResolveArrayIndexFromRegister(inst, baseExpr);
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → register-indexed load: {baseExpr.Emit()}[{indexExpr.Emit()}]");
                    
                    return new ExprIndex(ArrayAddressResolver.StripArrayDataOffset(baseExpr), indexExpr);
                }
                
                return baseExpr;
                // return new ExprMemory(baseExpr, 0);
            }
            var arrayExpr = TryMakeArrayAccess(baseExpr, src.Offset);
            if (arrayExpr != null)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → array access: {arrayExpr.Emit()}");
                return arrayExpr;
            }
            var multiDimExpr = TryMakeMultiDimAccess(baseExpr, src.Offset);
            if (multiDimExpr != null)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → multi-dim access: {multiDimExpr.Emit()}");
                return multiDimExpr;
            }

            // String length: [stringObj + 0x10] where base is a string literal
            if (src.Offset == 0x10 && IsStringLiteralExpr(baseExpr))
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → string.Length: {baseExpr.Emit()}.Length");
                return new ExprField(baseExpr, "Length");
            }

            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → raw memory: *([{baseExpr.Emit()} + 0x{src.Offset:X}])");
            return new ExprMemory(baseExpr, src.Offset);
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → ?load (non-memory src kind={src.Kind})");
        return new ExprLiteral("?load");
    }

    private ExprNode ApplyAnnotationTypeHint(ExprNode expr, string annotation, IrOperand src)
    {
        string? typeHint = ResolveAnnotationTypeHint(annotation, src);
        if (!string.IsNullOrWhiteSpace(typeHint))
            expr.InferredType = Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(typeHint, _ctx.Usings);
        ApplyAnnotationMetadataTypeHint(expr, annotation, src);
        return expr;
    }

    private void ApplyAnnotationMetadataTypeHint(ExprNode expr, string annotation, IrOperand src)
    {
        if (_typeModel == null || _method.TypeDefIndex < 0 || src.Kind != IrOperandKind.Memory)
            return;

        if (!annotation.StartsWith("this."))
            return;

        var field = _typeModel.ResolveFieldInfoAtOffset(_method.TypeDefIndex, (int)src.Offset);
        if (field == null || field.TypeIndex < 0)
            return;

        expr.MetadataTypeDefIndex = _typeModel.ResolveTypeDefIndexFromTypeIndex(field.TypeIndex);
    }

    private ExprTypeOf CreateTypeOfExpr(string typeName, IrInstruction inst)
    {
        var expr = new ExprTypeOf(typeName);
        if (_typeModel != null && inst.MetadataIndex >= 0)
            expr.MetadataTypeDefIndex = _typeModel.ResolveTypeDefIndexFromTypeIndex(inst.MetadataIndex);
        return expr;
    }

    private string? ResolveAnnotationTypeHint(string annotation, IrOperand src)
    {
        string? explicitHint = Rosetta.Analysis.Utils.StringUtils.ExtractTypeHint(annotation);
        if (!string.IsNullOrWhiteSpace(explicitHint))
            return explicitHint;

        if (_typeModel == null || _method.TypeDefIndex < 0 || src.Kind != IrOperandKind.Memory)
            return null;

        if (annotation.StartsWith("this."))
        {
            var field = _typeModel.ResolveFieldInfoAtOffset(_method.TypeDefIndex, (int)src.Offset);
            return field?.TypeName;
        }

        return null;
    }

    private ExprNode BuildLoadImm(IrInstruction inst)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      [DEBUG] BuildLoadImm called! sources={inst.Sources.Length}, opcode={inst.Opcode}");
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      BuildLoadImm: sources={inst.Sources.Length} ann=\"{inst.Annotation ?? "null"}\"");

        if (inst.Sources.Length == 0)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildLoadImm -> inst.Sources.Length == 0");
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → 0 (no sources)");
            return new ExprLiteral(0);
        }
        var src = inst.Sources[0];

        if (inst.Annotation != null && src.Kind != IrOperandKind.FloatImmediate && src.Kind != IrOperandKind.SimdImmediate)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildLoadImm -> inst.Annotation != null ({inst.Annotation})");
            var typeOrTypeOf = TryParseTypeOrTypeOf(inst.Annotation, inst);
            if (typeOrTypeOf != null) return typeOrTypeOf;
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildLoadImm -> Annotation fallback, creating ExprVar");
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → annotated: \"{inst.Annotation}\"");
            return new ExprVar(inst.Annotation);
        }

        if (src.Kind == IrOperandKind.Immediate)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildLoadImm -> src.Kind == IrOperandKind.Immediate");
            var val = FormatImm(src.Value, src.BitWidth);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → imm: {val} (raw=0x{src.Value:X} bits={src.BitWidth})");
            return new ExprLiteral(val);
        }
        if (src.Kind == IrOperandKind.FloatImmediate)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildLoadImm -> src.Kind == IrOperandKind.FloatImmediate");
            var val = FormatFloat(src.Value, src.BitWidth);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → float imm: {val} (raw=0x{src.Value:X} bits={src.BitWidth})");
            return new ExprLiteral(val);
        }
        if (src.Kind == IrOperandKind.SimdImmediate)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildLoadImm -> Found SIMD Immediate! Creating ExprSimd node. Hi: 0x{src.Offset:X16}, Lo: 0x{src.Value:X16}");
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → simd imm: 0x{src.Offset:X16} 0x{src.Value:X16}");
            return new ExprSimd(src.Value, src.Offset);
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildLoadImm -> fallback to 0. kind={src.Kind}");
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → 0 (fallback, kind={src.Kind})");
        return new ExprLiteral(0);
    }

    private ExprNode BuildLoadAddr(IrInstruction inst)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"      BuildLoadAddr: ann=\"{inst.Annotation ?? "null"}\" sources={inst.Sources.Length}");

        if (inst.Annotation != null)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → annotated: \"{inst.Annotation}\"");
            return new ExprVar(inst.Annotation);
        }
        if (inst.Sources.Length > 0)
        {
            var result = GetSourceExpr(inst, 0);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → source[0]: {result.Emit()}");
            return result;
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → 0 (fallback)");
        return new ExprLiteral(0);
    }

}