using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.Resolve;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

public sealed partial class ExprPropagator
{
    /// <summary>
    /// Attempts to build an array creation expression (1D or Multi-Dimensional).
    /// </summary>
    private ExprNode? TryHandleArrayCreation(string annotation)
    {
        if (annotation.StartsWith("new ") && annotation.Contains('['))
        {
            int lastClose = annotation.LastIndexOf(']');
            int lastOpen = annotation.LastIndexOf('[', lastClose);
            if (lastOpen >= 0 && lastClose > lastOpen)
            {
                string dimsStr = annotation[(lastOpen + 1)..lastClose];
                string arrType = annotation[4..lastOpen];

                // Multi-dim arrays: "new int[2, 2]" or "new int[,]"
                if (dimsStr.Contains(','))
                {
                    string[] dimParts = dimsStr.Split(',', StringSplitOptions.TrimEntries);
                    string dimList = string.Join(", ", dimParts.Where(dp => int.TryParse(dp, out _)));
                    string cleanType = Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(arrType, _ctx.Usings);
                    var mdExpr = new ExprNew(cleanType, new ExprVar(dimList));
                    mdExpr.IsMultiDim = true;
                    mdExpr.InferredType = cleanType + "[,]";
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → new multi-dim array: new {cleanType}[{dimList}]");
                    return mdExpr;
                }

                // 1D arrays: "new int[5]"
                int.TryParse(dimsStr, out int size);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → new array: type={arrType} size={size}");
                var arrayExpr = new ExprNew(Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(arrType, _ctx.Usings), new ExprLiteral(size));
                arrayExpr.InferredType = Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(arrType, _ctx.Usings) + "[]";
                return arrayExpr;
            }
        }
        return null;
    }

    /// <summary>
    /// Handles C# 'is' and 'cast' operators.
    /// </summary>
    private ExprNode? TryHandleTypeChecks(string annotation, IrInstruction inst)
    {
        // Handle is<T> — C# 'x is T' (IL2CPP isinst / IsInst)
        if (annotation.StartsWith("is<") && annotation.EndsWith(">"))
        {
            string isType = Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(annotation[3..^1], _ctx.Usings);
            var objExpr = inst.Sources.Length > 1 ? GetSourceExpr(inst, 1) : new ExprVar("obj");
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → {objExpr.Emit()} is {isType}");
            return new ExprBinary("is", objExpr, new ExprVar(isType));
        }

        // Handle cast<T> — C# '(T)x' (IL2CPP castclass / CastClass)
        if (annotation.StartsWith("cast<") && annotation.EndsWith(">"))
        {
            string castTypeName = Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(annotation[5..^1], _ctx.Usings);
            var objExpr = inst.Sources.Length > 1 ? GetSourceExpr(inst, 1) : new ExprVar("obj");
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → ({castTypeName}){objExpr.Emit()}");
            return new ExprCast(castTypeName, objExpr);
        }

        return null;
    }

    /// <summary>
    /// Fallback resolution for struct_box_local across blocks.
    /// </summary>
    private ExprNode ResolveStructBoxLocal(IrInstruction inst)
    {
        if (_ctx.CurrentBlockId >= 0 &&
            BlockStatements.TryGetValue(_ctx.CurrentBlockId, out var boxLocalStmts) &&
            boxLocalStmts.Count > 0)
        {
            for (int si = boxLocalStmts.Count - 1; si >= Math.Max(0, boxLocalStmts.Count - 10); si--)
            {
                var stmt = boxLocalStmts[si];
                if (stmt.IsDeclaration &&
                    stmt.Expr is ExprAssign declAssign &&
                    declAssign.Target is ExprVar declVar &&
                    stmt.Inst?.ResultType != null &&
                    Rosetta.Analysis.Utils.StringUtils.IsStructResultType(stmt.Inst.ResultType))
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → struct_box_local: referencing {declVar.Name} (type={stmt.Inst.ResultType})");
                    return new ExprVar(declVar.Name);
                }
            }
        }

        // Search predecessor blocks
        foreach (var (blockId, stmts) in BlockStatements)
        {
            if (blockId == _ctx.CurrentBlockId) continue;
            for (int si = stmts.Count - 1; si >= Math.Max(0, stmts.Count - 10); si--)
            {
                var stmt = stmts[si];
                if (stmt.IsDeclaration &&
                    stmt.Expr is ExprAssign declAssign &&
                    declAssign.Target is ExprVar declVar &&
                    stmt.Inst?.ResultType != null &&
                    Rosetta.Analysis.Utils.StringUtils.IsStructResultType(stmt.Inst.ResultType))
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → struct_box_local cross-block: referencing {declVar.Name} (type={stmt.Inst.ResultType})");
                    return new ExprVar(declVar.Name);
                }
            }
        }

        // Fallback: check stack slot values if we can resolve SP offset
        if (_ctx.StackSlotValues.Count > 0)
        {
            long spOffset = -1;
            if (inst.Sources.Length > 1)
            {
                var argExpr = GetSourceExpr(inst, 1);
                spOffset = GetSpOffset(argExpr);
            }
            if (spOffset >= 0 && _ctx.StackSlotValues.TryGetValue(spOffset, out var slotVal))
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → struct_box_local from SP slot 0x{spOffset:X}: {slotVal.Emit()}");
                return slotVal;
            }

            var candidates = new List<ExprNode>();
            foreach (var val in _ctx.StackSlotValues.Values)
            {
                if (val.StaticFieldHint != null || val is ExprField || val is ExprNew || val is ExprStructInit)
                {
                    candidates.Add(val);
                }
            }
            if (candidates.Count == 1)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → struct_box_local resolved to single stack value: {candidates[0].Emit()}");
                return candidates[0];
            }
            else if (candidates.Count > 1)
            {
                var lastCandidate = candidates[^1];
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → struct_box_local resolved to last stack value: {lastCandidate.Emit()}");
                return lastCandidate;
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → fallback struct_box_local()");
        return new ExprCall("struct_box_local");
    }

    /// <summary>
    /// Trace the MethodInfo* argument back through SSA to find MethodRef annotations
    /// containing generic type parameters (e.g., "AddComponent&lt;BoxCollider&gt;").
    /// </summary>
    private string? TryExtractGenericArgs(IrInstruction callInst, int methodInfoSrcIdx)
    {
        // Get the SSA variable for the MethodInfo* argument
        var ssaVar = _ssa.GetSource(callInst.Address, methodInfoSrcIdx);
        if (!ssaVar.HasValue) return null;

        // Walk back through the SSA def chain looking for MethodRef annotations
        var current = ssaVar.Value;
        for (int depth = 0; depth < 4; depth++)
        {
            // Find the defining instruction via DefSites
            if (!_ssa.DefSites.TryGetValue(current, out var defSite)) break;
            if (defSite.instrIndex < 0) break; // phi node, no instruction
            var defBlock = _cfg.FindBlock(defSite.blockId);
            if (defBlock == null || defSite.instrIndex >= defBlock.Instructions.Count) break;
            var defInst = defBlock.Instructions[defSite.instrIndex];

            // Check if this Load/Assign has a MethodRef annotation with generics
            string? ann = defInst.Annotation;
            if (ann != null && ann.Contains("MethodRef(") && ann.Contains("<"))
            {
                // Extract generic args: "MethodRef(Foo::Bar<Baz>)" → "<Baz>"
                int methodStart = ann.IndexOf("::");
                if (methodStart < 0) methodStart = ann.IndexOf("(");
                string methodPart = ann[(methodStart + 2)..];

                int genStart = methodPart.IndexOf('<');
                int genEnd = methodPart.LastIndexOf('>');
                if (genStart >= 0 && genEnd > genStart)
                {
                    string genericType = methodPart[(genStart + 1)..genEnd];
                    string cleaned = Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(genericType, _ctx.Usings);
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          extracted generic: <{cleaned}> from {ann}");
                    return $"<{cleaned}>";
                }
            }

            // Trace through Load/Assign: follow the source SSA variable
            if (defInst.Opcode is IrOpcode.Load or IrOpcode.Assign && defInst.Sources.Length > 0)
            {
                // For Load [base], try the memory base
                if (defInst.Opcode == IrOpcode.Load && _ssa.MemoryBaseMap.TryGetValue((defInst.Address, 0), out var memBase))
                {
                    current = memBase;
                    continue;
                }

                var nextSsa = _ssa.GetSource(defInst.Address, 0);
                if (nextSsa.HasValue)
                {
                    current = nextSsa.Value;
                    continue;
                }
            }
            break;
        }

        return null;
    }

    /// <summary>
    /// If the target expression is a static_fields pointer (or static_fields + offset),
    /// resolve it to the actual static field name using the TypeModel.
    ///
    /// IL2CPP passes the address of a static field as 'this' to value-type instance
    /// methods like Int32::ToString or Single::ToString. This method converts:
    ///   static_fields       → TestVariables.staticVar
    ///   static_fields + 4   → TestVariables.staticReadonlyVar
    /// </summary>
    private ExprNode? TryResolveStaticFieldPointer(ExprNode target)
    {
        int offset = -1;
        int ownerTypeDefIdx = _method.TypeDefIndex;

        // Pattern 1: ExprVar("static_fields") → offset 0
        if (target is ExprVar v && v.Name == "static_fields")
        {
            offset = 0;
        }
        // Pattern 2: ExprBinary("+", ExprVar("static_fields"), ExprLiteral(N)) → offset N
        else if (target is ExprBinary bin && bin.Op == "+" &&
                 bin.Left is ExprVar lv && lv.Name == "static_fields" &&
                 bin.Right is ExprLiteral lit && lit.Value is int intVal)
        {
            offset = intVal;
        }
        else if (target is ExprBinary bin2 && bin2.Op == "+" &&
                 bin2.Left is ExprVar lv2 && lv2.Name == "static_fields" &&
                 bin2.Right is ExprLiteral lit2 && lit2.Value is long longVal)
        {
            offset = (int)longVal;
        }

        if (offset < 0 || _typeModel == null) return null;

        // Try the declaring type first, then search all types
        var field = _typeModel.ResolveStaticFieldAtOffset(ownerTypeDefIdx, offset);
        if (field != null)
        {
            string cleanType = _method.DeclaringType ?? "Type";
            // Strip namespace for cleaner output
            int lastDot = cleanType.LastIndexOf('.');
            if (lastDot >= 0) cleanType = cleanType[(lastDot + 1)..];
            return new ExprVar($"{cleanType}.{field.Name}");
        }

        return null;
    }

    // ── Pointer aliasing recovery helpers ─────────────────────────────────

    /// <summary>
    /// Recovers pointer-aliased field access at offset 0.
    /// When a struct's first field shares the same memory address as the struct itself,
    /// IL2CPP passes the struct pointer directly to methods expecting the field's type.
    /// Detects the type mismatch and wraps with ExprField recursively.
    /// Returns null if no aliasing detected (caller keeps original target).
    /// </summary>
    private string? ResolveExprType(ExprNode expr)
    {
        if (expr == null) return null;
        if (!string.IsNullOrEmpty(expr.InferredType))
            return expr.InferredType;

        if (expr is ExprField field)
        {
            string? parentType = ResolveExprType(field.Target);
            if (parentType == null && field.Target is ExprThis)
            {
                parentType = _method.DeclaringType;
            }

            if (parentType != null && _typeModel != null)
            {
                if (_typeModel.FieldLayoutsByTypeName.TryGetValue(parentType, out int parentTypeDefIdx))
                {
                    var layout = _typeModel.GetLayoutForTypeName(parentType);
                    if (layout != null)
                    {
                        var fe = layout.Fields.FirstOrDefault(f => f.Name == field.FieldName && !f.IsStatic);
                        if (fe != null)
                            return fe.TypeName;
                    }
                }
            }
        }

        if (expr is ExprVar v)
        {
            if (v.Name.StartsWith("this."))
            {
                string fieldName = v.Name["this.".Length..];
                string? parentType = _method.DeclaringType;
                if (parentType != null && _typeModel != null)
                {
                    if (_typeModel.FieldLayoutsByTypeName.TryGetValue(parentType, out int parentTypeDefIdx))
                    {
                        var layout = _typeModel.GetLayoutForTypeName(parentType);
                        if (layout != null)
                        {
                            var fe = layout.Fields.FirstOrDefault(f => f.Name == fieldName && !f.IsStatic);
                            if (fe != null)
                                return fe.TypeName;
                        }
                    }
                }
            }

            if (_method.MethodIndex >= 0 && _typeModel != null &&
                _typeModel.Signatures.TryGetValue(_method.MethodIndex, out var currentSig))
            {
                foreach (var param in currentSig.Parameters)
                {
                    if (param.Name == v.Name)
                        return param.TypeName;
                }
            }
        }

        return null;
    }

    private ExprNode? TryRecoverAliasedField(ExprNode target, string declaringTypeName)
    {
        if (_typeModel == null) return null;

        string? paramTypeName = ResolveExprType(target);
        if (paramTypeName == null) return null;

        // Strip in/ref/out prefix to get the bare type name
        string bareTypeName = StripByRefPrefix(paramTypeName);

        // Fast early exit: same type = no aliasing (most common case)
        if (bareTypeName == declaringTypeName) return null;
        // Only pay for alias normalization when types differ
        if (TypeNamesMatch(bareTypeName, declaringTypeName)) return null;

        // Find the FieldLayout for the parameter's type by matching TypeName — O(1)
        if (!_typeModel.FieldLayoutsByTypeName.TryGetValue(bareTypeName, out int typeDefIndex))
            return null;

        // Recursively resolve through offset-0 fields until we match the declaring type
        return ResolveAliasedFieldChain(target, typeDefIndex, declaringTypeName, 0);
    }

    /// <summary>
    /// Recursively walks field-at-offset-0 chain until the field's type matches the declaring type.
    /// Handles nested structs: s → s.inner → s.inner.x
    /// Returns null if the chain doesn't resolve.
    /// </summary>
    private ExprNode? ResolveAliasedFieldChain(ExprNode expr, int typeDefIndex, string declaringTypeName, int depth)
    {
        if (depth > 8) return null; // safety against pathological nesting

        var fieldInfo = GetFirstInstanceField(typeDefIndex);
        if (fieldInfo == null) return null;

        // Check if this field's type matches the declaring type
        if (TypeNamesMatch(fieldInfo.TypeName, declaringTypeName))
        {
            if (ConsoleReporter.IsTracing)
                ConsoleReporter.Debug($"          alias recovery: {expr.Emit()}.{fieldInfo.Name} (depth={depth})");
            return new ExprField(expr, fieldInfo.Name);
        }

        // The offset-0 field is itself a struct — find its layout and recurse — O(1)
        if (_typeModel != null && _typeModel.FieldLayoutsByTypeName.TryGetValue(fieldInfo.TypeName, out int nestedIdx))
        {
            var wrapped = new ExprField(expr, fieldInfo.Name);
            return ResolveAliasedFieldChain(wrapped, nestedIdx, declaringTypeName, depth + 1);
        }

        return null;
    }

    /// <summary>
    /// Compare two type names accounting for CLR↔C# keyword differences.
    /// FieldLayouts use C# keywords ("int"), annotations use CLR names ("System.Int32").
    /// </summary>
    private static bool TypeNamesMatch(string a, string b)
    {
        if (a == b) return true;
        // Normalize both via alias table: "System.Int32" → "int", "System.Single" → "float"
        return Common.TypeUtils.ToAlias(a) == Common.TypeUtils.ToAlias(b);
    }

    /// <summary>
    /// Strip in/ref/out prefix and byref ampersand from a type name.
    /// "in SimpleStruct" → "SimpleStruct", "ref int&" → "int"
    /// </summary>
    private static string StripByRefPrefix(string typeName) => Common.TypeUtils.StripModifiers(typeName);

    /// <summary>
    /// Get the first instance (non-static) field of a type by its minimum metadata offset.
    /// IL2CPP metadata stores field offsets including the object header (16 bytes on ARM64),
    /// so the first field of a value type is at offset 16, not 0.
    /// </summary>
    private Rosetta.Model.FieldLayout.FieldEntry? GetFirstInstanceField(int typeDefIndex)
    {
        if (_typeModel == null || !_typeModel.FieldLayouts.TryGetValue(typeDefIndex, out var layout))
            return null;

        Rosetta.Model.FieldLayout.FieldEntry? first = null;
        foreach (var f in layout.Fields)
        {
            if (f.IsStatic || f.Offset < 0) continue;
            if (first == null || f.Offset < first.Offset)
                first = f;
        }
        return first;
    }

    private bool TryInterceptRuntimeInternalsAndMemcpy(string annotation, IrInstruction inst, out ExprNode? result)
    {
        result = null;
        // Suppress IL2CPP runtime internals that have no C# equivalent
        if (annotation.Contains("gc_write_barrier") || annotation == "string_intern")
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → suppress runtime internal: {annotation}");
            return true;
        }

        // Intercept memcpy for struct copying on the stack
        if (annotation == "memcpy")
        {
            var destExpr = GetRegExpr(0, inst.Address, -1);
            var srcExpr = GetRegExpr(1, inst.Address, -1);
            
            if (StackOffsetResolver.TryGetOffset(destExpr, out long destOffset))
            {
                if (StackOffsetResolver.TryGetOffset(srcExpr, out long srcOffset) && _ctx.StackSlotValues.TryGetValue(srcOffset, out var srcValue))
                {
                    _ctx.StackSlotValues[destOffset] = srcValue;
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → memcpy intercept: [SP+0x{destOffset:X}] = {srcValue.Emit()} (from SP+0x{srcOffset:X})");
                }
                else
                {
                    _ctx.StackSlotValues[destOffset] = srcExpr;
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → memcpy intercept: [SP+0x{destOffset:X}] = {srcExpr.Emit()}");
                }
            }
            return true; // Suppress the memcpy call in the AST
        }
        return false;
    }

    private ExprNode? ResolveCallTarget(string? annotation, IrInstruction inst, string? typeName, bool isStatic)
    {
        ExprNode? target = null;
        // Resolve 'this'
        if (!isStatic && typeName != null && inst.Sources.Length > 1)
        {
            var thisExpr = GetSourceExpr(inst, 1);
            if (IsThisExpr(thisExpr) || IsSameType(typeName))
            {
                target = new ExprThis();
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          this → ExprThis");
            }
            else
            {
                target = StackSlotResolver.ResolveThis(thisExpr, _ctx);
            }
        }
        else if (!isStatic && typeName != null && inst.Opcode is IrOpcode.IndirectCall or IrOpcode.TailCall)
        {
            var thisExpr = GetRegExpr(0, inst.Address, -1);
            if (IsThisExpr(thisExpr) || IsSameType(typeName))
                target = new ExprThis();
            else
                target = thisExpr;
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          indirect this → {target.Emit()}");
        }
        else if (typeName != null)
        {
            target = new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(typeName, _ctx.Usings));
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          static target → {target.Emit()}");
        }

        // ── Resolve static_fields pointer to actual field name ──────────────
        if (target != null && _typeModel != null && _method.TypeDefIndex >= 0)
        {
            var resolved = TryResolveStaticFieldPointer(target);
            if (resolved != null)
            {
                target = resolved;
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          static_fields resolved → {target.Emit()}");
            }
        }

        // ── Pointer aliasing recovery (type punning at offset 0) ──────────────
        if (target != null && !isStatic && typeName != null && _typeModel != null)
        {
            var recovered = TryRecoverAliasedField(target, typeName);
            if (recovered != null)
                target = recovered;
        }

        if (target != null)
        {
            target = SimplifyArrayPointerTarget(target);
        }

        return target;
    }

    private ExprNode SimplifyArrayPointerTarget(ExprNode expr)
    {
        if (expr is ExprBinary { Op: "+", Right: ExprLiteral lit } bin &&
            lit.Value is int or long &&
            bin.Left.InferredType != null &&
            Rosetta.Common.TypeUtils.IsArray(bin.Left.InferredType))
        {
            long offset = lit.Value is int i ? i : (long)lit.Value;
            if (offset >= Rosetta.Common.Constants.ArrayDataOffset)
            {
                long dataOffset = offset - Rosetta.Common.Constants.ArrayDataOffset;
                int elemSize = ResolveElementSize(bin.Left.InferredType);
                if (elemSize > 0 && dataOffset % elemSize == 0)
                {
                    int index = (int)(dataOffset / elemSize);
                    return new ExprIndex(bin.Left, new ExprLiteral(index))
                    {
                        InferredType = Rosetta.Common.TypeUtils.GetArrayElementType(bin.Left.InferredType)
                    };
                }
            }
        }
        return expr;
    }

    private List<ExprNode> CollectCallArguments(
        IrInstruction inst, int firstArgSource, int lastArgSource, 
        IReadOnlyList<Rosetta.Model.MethodSignature.ParamEntry>? parameters)
    {
        var args = new List<ExprNode>();
        for (int s = firstArgSource; s <= lastArgSource; s++)
        {
            var argExpr = GetSourceExpr(inst, s);

            // Strip the implicit MethodInfo* null argument at the end
            if (s == lastArgSource && argExpr is ExprLiteral lit && (lit.Value is 0 or null))
            {
                int paramCount = parameters?.Count ?? 0;
                int currentArgs = lastArgSource - firstArgSource + 1;
                if (currentArgs > paramCount)
                {
                    lastArgSource--;
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          strip MethodInfo* (SSA null check)");
                    continue; // Skip processing this argument entirely
                }
            }
            args.Add(argExpr);
        }

        // Strip any MethodRef values that leaked through as call arguments
        args.RemoveAll(a => a is ExprVar mv && mv.Name.StartsWith("MethodRef("));
        return args;
    }

    private void ReorderAndDecorateArguments(
        List<ExprNode> args, IrInstruction inst, int firstArgSource, string methodName, 
        IReadOnlyList<Rosetta.Model.MethodSignature.ParamEntry>? parameters)
    {
        // ── HFA argument grouping ──
        HfaArgumentGrouper.Group(args, inst, firstArgSource, methodName, parameters);

        // ── Stack-passed argument recovery (AAPCS overflow) ──
        if (parameters != null && parameters.Count > args.Count && _ctx.StackSlotValues.Count > 0)
        {
            int missing = parameters.Count - args.Count;
            foreach (var (offset, stackVal) in _ctx.StackSlotValues.OrderBy(kv => kv.Key))
            {
                if (missing <= 0) break;
                if (offset < 0) continue; // skip negative offsets (callee-save spills)
                args.Add(stackVal);
                missing--;
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          stack arg [SP+0x{offset:X}] → {stackVal.Emit()}");
            }
        }

        // ── Parameter Reordering (AAPCS) ──
        if (parameters != null && parameters.Count == args.Count && args.Count > 1)
        {
            var reordered = new List<ExprNode>();
            var gpArgs = new Queue<ExprNode>();
            var fpArgs = new Queue<ExprNode>();

            int expectedGp = 0;
            foreach (var p in parameters)
            {
                bool isFp = p.TypeName is "float" or "System.Single" or "double" or "System.Double" || p.HfaSize >= 2;
                if (!isFp) expectedGp++;
            }

            for (int i = 0; i < args.Count; i++)
            {
                if (i < expectedGp) gpArgs.Enqueue(args[i]);
                else fpArgs.Enqueue(args[i]);
            }

            foreach (var p in parameters)
            {
                bool isFp = p.TypeName is "float" or "System.Single" or "double" or "System.Double" || p.HfaSize >= 2;
                if (isFp && fpArgs.Count > 0)
                    reordered.Add(fpArgs.Dequeue());
                else if (!isFp && gpArgs.Count > 0)
                    reordered.Add(gpArgs.Dequeue());
            }

            if (reordered.Count == args.Count)
            {
                args.Clear();
                args.AddRange(reordered);
            }
        }

        // ── Detect out/ref parameters ──
        for (int i = 0; i < args.Count; i++)
        {
            args[i] = OutParameterDetector.Detect(args[i], ExprMap, _ctx, parameters, i);
        }

        // ── Universal metadata-driven parameter type coercion ──
        TypeCoercer.Coerce(args, parameters);
    }

    private ExprNode? TryHandleStructReturn(
        IrInstruction inst, ExprNode callExpr, int? methodIndex, 
        IReadOnlyList<Rosetta.Model.MethodSignature.ParamEntry>? parameters)
    {
        // ── Map HFA Multi-Register Returns to Implicit Clobbers ──
        if (methodIndex.HasValue && _typeModel != null && _typeModel.Signatures.TryGetValue(methodIndex.Value, out var hfaSig))
        {
            if (hfaSig.ReturnHfaSize >= 2 && hfaSig.ReturnHfaFieldNames != null)
            {
                var tempVar = new ExprVar($"hfa_ret_{inst.Address:X}");
                tempVar.InferredType = hfaSig.ReturnTypeName;

                _ctx.BlockStatements[_ctx.CurrentBlockId].Add(new ExprStatement 
                { 
                    Expr = new ExprAssign(tempVar, callExpr),
                    Inst = inst,
                    IsDeclaration = true
                });

                for (int h = 0; h < hfaSig.ReturnHfaSize; h++)
                {
                    int regVarId = 100 + h; // s0, s1, etc.
                    if (_ssa.OperandMap.TryGetValue((inst.Address, -(regVarId + 2)), out var clobberedSsa))
                    {
                        if (h < hfaSig.ReturnHfaFieldNames.Length)
                        {
                            var fieldExpr = new ExprField(tempVar, hfaSig.ReturnHfaFieldNames[h]);
                            ExprMap[clobberedSsa] = fieldExpr;
                            
                            if (DeadCodeEliminator.ShouldEliminate(inst, clobberedSsa, fieldExpr, _defUse, _ssa, _cfg, _ctx, out bool shouldInline, out _))
                            {
                                if (shouldInline)
                                {
                                    Inlined.Add(clobberedSsa);
                                }
                            }
                            else
                            {
                                var assignTarget = MakeVarExpr(clobberedSsa);
                                _ctx.BlockStatements[_ctx.CurrentBlockId].Add(new ExprStatement 
                                {
                                    Expr = new ExprAssign(assignTarget, fieldExpr),
                                    Inst = inst,
                                    IsDeclaration = IsFirstDefinition(clobberedSsa),
                                    SsaVar = clobberedSsa
                                });
                            }
                        }
                    }
                }
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          HFA return natively emitted ({hfaSig.ReturnHfaSize} fields)");
                return null;
            }
        }

        // Handle ARM64 AAPCS Large Struct Return deterministically
        ExprNode? structRetPtr = null;
        var block = _cfg.FindBlock(_ctx.CurrentBlockId);
        if (block != null)
        {
            int idx = block.Instructions.IndexOf(inst);
            if (idx >= 0)
            {
                for (int i = idx - 1; i >= System.Math.Max(0, idx - 8); i--)
                {
                    var prev = block.Instructions[i];
                    var dest = _ssa.GetDestination(prev.Address);
                    if (dest.HasValue && dest.Value.VarId == 8 && !dest.Value.IsFloat)
                    {
                        ExprMap.TryGetValue(dest.Value, out structRetPtr);
                        break;
                    }
                    if (prev.Opcode is IrOpcode.Call or IrOpcode.IndirectCall) break;
                }
            }
        }

        long? spOffsetRet = structRetPtr != null && StackOffsetResolver.TryGetOffset(structRetPtr, out long retOffset)
            ? retOffset
            : null;

        if (spOffsetRet.HasValue)
        {
            bool returnsStruct = false;
            string? returnTypeName = null;
            if (methodIndex.HasValue && _typeModel != null && _typeModel.Signatures.TryGetValue(methodIndex.Value, out var sig))
            {
                bool isStructType = Rosetta.Analysis.Utils.StringUtils.IsStructResultType(sig.ReturnTypeName);
                returnsStruct = isStructType && sig.ReturnHfaSize == 0;
                returnTypeName = sig.ReturnTypeName;
            }

            if (returnsStruct)
            {
                if (returnTypeName != null)
                    _ctx.StackStructReturnTypes[spOffsetRet.Value] = returnTypeName;

                var destSsa = _ssa.GetDestination(inst.Address);
                if (destSsa.HasValue)
                {
                    _ctx.StackSlotValues[spOffsetRet.Value] = new ExprVar(GetVarName(destSsa.Value));
                }
                else
                {
                    _ctx.StackSlotValues[spOffsetRet.Value] = callExpr;
                }
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          struct return -> [SP+0x{spOffsetRet.Value:X}]");
            }
        }

        return callExpr;
    }
}
