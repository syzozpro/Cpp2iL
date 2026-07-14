using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;
using Rosetta.Analysis.Utils;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Detects value type and primitive type boxing patterns (e.g. box&lt;T&gt; or il2cpp_object_new) and simplifies them.
///
/// Decision logic uses only typed ExprNode checks — no .Emit(), .ToString(), .StartsWith(), .Contains(), or Regex.
/// </summary>
public static class BoxingDetector
{
    /// <summary>
    /// Check for primitive type boxing ("new int()", "new float()" etc. or il2cpp_object_new).
    /// </summary>
    public static ExprNode? TryDetectPrimitiveBox(string newTypeName, PropagationContext ctx)
    {
        if (ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"BoxingDetector: TryDetectPrimitiveBox called for type '{newTypeName}'");
        }
        if (ExprUtils.IsPrimitiveBoxType(newTypeName) &&
            ctx.CurrentBlockId >= 0 &&
            ctx.BlockStatements.TryGetValue(ctx.CurrentBlockId, out var boxStmts) &&
            boxStmts.Count > 0)
        {
            for (int si = boxStmts.Count - 1; si >= Math.Max(0, boxStmts.Count - 4); si--)
            {
                if (boxStmts[si].Expr is ExprAssign assign && assign.Target is ExprSpSlot)
                {
                    var boxedValue = assign.Value;
                    boxStmts.RemoveAt(si);

                    boxedValue = BoxingValueNormalizer.Normalize(newTypeName, boxedValue);

                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → primitive box new {newTypeName}() → {boxedValue.Emit()}");
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Debug($"BoxingDetector: Detected primitive box for '{newTypeName}' -> '{boxedValue.Emit()}'");
                    }
                    return boxedValue;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Try to recover/wire struct or value type boxing call box&lt;T&gt;.
    /// </summary>
    public static ExprNode TryDetectValueTypeBox(string annotation, IrInstruction inst, SsaContext ssa,
        Dictionary<SsaVariable, ExprNode> exprMap, PropagationContext ctx, Func<IrInstruction, int, ExprNode> getSourceExpr)
    {
        string boxType = ExprUtils.CleanTypeName(annotation[4..^1]);
        if (ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"BoxingDetector: TryDetectValueTypeBox called for annotation '{annotation}' (type: '{boxType}')");
        }

        // Fast path: wired source from FuseStructBoxing (pre-SSA pass)
        // The boxing call was fused with the struct-returning call — source[1] is the struct value.
        if (inst.Sources.Length > 1)
        {
            var structExpr = getSourceExpr(inst, 1);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → box<{boxType}> wired: {structExpr.Emit()}");
            return structExpr;
        }

        bool isStructType = Rosetta.Analysis.Utils.StringUtils.IsStructResultType(boxType);
        if (ctx.CurrentBlockId >= 0 &&
            ctx.BlockStatements.TryGetValue(ctx.CurrentBlockId, out var currentStmts) &&
            currentStmts.Count > 0)
        {
            if (isStructType)
            {
                var spStores = new List<(int idx, long offset, ExprNode value)>();
                for (int si = currentStmts.Count - 1; si >= Math.Max(0, currentStmts.Count - 6); si--)
                {
                    if (currentStmts[si].Expr is ExprAssign assign && assign.Target is ExprSpSlot sp)
                    {
                        spStores.Add((si, sp.Offset, assign.Value));
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          found SP store: idx={si} offset=0x{sp.Offset:X} value={assign.Value.Emit()}");
                    }
                }
                if (spStores.Count > 0)
                {
                    var recent = new List<(int idx, long offset, ExprNode value)> { spStores[0] };
                    for (int k = 1; k < spStores.Count; k++)
                    {
                        long gap = Math.Abs(spStores[k].offset - recent[^1].offset);
                        if (gap > 0 && gap <= 8)
                            recent.Add(spStores[k]);
                    }
                    recent.Sort((a, b) => a.offset.CompareTo(b.offset));
                    var contiguous = recent;
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          contiguous stores: {contiguous.Count}");

                    if (contiguous.Count <= 4)
                    {
                        foreach (var s in contiguous.OrderByDescending(s => s.idx))
                            currentStmts.RemoveAt(s.idx);

                        bool isEnum = false;
                        if (ctx.TypeModel != null && inst.BoxedTypeDefIndex >= 0)
                        {
                            var td = ctx.TypeModel.GetTypeDef(inst.BoxedTypeDefIndex);
                            if (td != null) isEnum = td.IsEnum;
                        }
                        else if (ctx.TypeModel != null && ctx.TypeModel.FieldLayoutsByTypeName.TryGetValue(boxType, out int tdIdx))
                        {
                            var td = ctx.TypeModel.GetTypeDef(tdIdx);
                            if (td != null) isEnum = td.IsEnum;
                        }

                        var components = new List<ExprNode>();
                        foreach (var (_, _, originalValue) in contiguous)
                        {
                            var value = originalValue;
                            
                            // Resolve SSA variables that were not inlined (e.g. x26_1 -> ExprSpSlot)
                            // We do this in O(1) by constructing an SsaVariable key, since Equals() only checks VarId and Version.
                            if (value is ExprVar evarResolve && evarResolve.VarId != -1)
                            {
                                var lookupKey = new Rosetta.Analysis.IR.SSA.SsaVariable(evarResolve.VarId, evarResolve.Version, 0);
                                if (exprMap.TryGetValue(lookupKey, out var underlying))
                                {
                                    value = underlying;
                                }
                            }

                            if (value is ExprSpSlot spSlot && ctx.StackSlotValues.TryGetValue(spSlot.Offset, out var spSlotVal))
                            {
                                value = spSlotVal;
                            }

                            if (isEnum)
                            {
                                components.Add(value);
                                continue;
                            }

                            if (value is ExprSimd simd)
                            {
                                var slots = simd.GetSlots32();

                                int count = 4;
                                if (ctx.TypeModel != null)
                                {
                                    var layout = FindLayoutByShortName(ctx.TypeModel, boxType);

                                    if (layout != null && layout.InstanceFieldCount > 0 && layout.InstanceFieldCount <= 4)
                                    {
                                        count = layout.InstanceFieldCount;
                                    }
                                }

                                for (int i = 0; i < count; i++)
                                {
                                    float f = BitConverter.Int32BitsToSingle((int)slots[i]);
                                    components.Add(new ExprLiteral(f));
                                }
                            }
                            else if (value is ExprNew ne && ne.Args.Count > 0 && ne.Size == null)
                            {
                                // Structural: decompose constructor args directly from ExprNew.Args
                                foreach (var arg in ne.Args)
                                {
                                    var argCopy = arg;
                                    if (value.StaticFieldHint != null)
                                        argCopy.StaticFieldHint = value.StaticFieldHint;
                                    components.Add(argCopy);
                                }
                            }
                            else if (value is ExprLiteral lit && lit.Value is double d)
                            {
                                int expectedCount = 2; // Default for 64-bit float pair (e.g. Vector2)
                                if (ctx.TypeModel != null)
                                {
                                    var layout = FindLayoutByShortName(ctx.TypeModel, boxType);
                                    if (layout != null && layout.InstanceFieldCount > 0 && layout.InstanceFieldCount <= 2)
                                    {
                                        expectedCount = layout.InstanceFieldCount;
                                    }
                                }

                                if (expectedCount == 2)
                                {
                                    long raw = BitConverter.DoubleToInt64Bits(d);
                                    float f0 = BitConverter.Int32BitsToSingle((int)(raw & 0xFFFFFFFF));
                                    float f1 = BitConverter.Int32BitsToSingle((int)(raw >> 32));
                                    components.Add(new ExprLiteral(f0));
                                    components.Add(new ExprLiteral(f1));
                                }
                                else
                                {
                                    components.Add(value);
                                }
                            }
                            else if (value is ExprLiteral litInt && (litInt.Value is int || litInt.Value is uint))
                            {
                                int intVal = litInt.Value is int i ? i : (int)(uint)litInt.Value;
                                float f = BitConverter.Int32BitsToSingle(intVal);
                                components.Add(new ExprLiteral(f));
                            }
                            else if (value is ExprLiteral litLong && (litLong.Value is long || litLong.Value is ulong))
                            {
                                int expectedCount = 2;
                                if (ctx.TypeModel != null)
                                {
                                    var layout = FindLayoutByShortName(ctx.TypeModel, boxType);
                                    if (layout != null && layout.InstanceFieldCount > 0 && layout.InstanceFieldCount <= 2)
                                    {
                                        expectedCount = layout.InstanceFieldCount;
                                    }
                                }

                                long raw = litLong.Value is long l ? l : (long)(ulong)litLong.Value;
                                if (expectedCount == 2)
                                {
                                    float f0 = BitConverter.Int32BitsToSingle((int)(raw & 0xFFFFFFFF));
                                    float f1 = BitConverter.Int32BitsToSingle((int)(raw >> 32));
                                    components.Add(new ExprLiteral(f0));
                                    components.Add(new ExprLiteral(f1));
                                }
                                else
                                {
                                    float f = BitConverter.Int32BitsToSingle((int)(raw & 0xFFFFFFFF));
                                    components.Add(new ExprLiteral(f));
                                }
                            }
                            else if (value is ExprVar evar && evar.Name.EndsWith("d") && double.TryParse(evar.Name.Substring(0, evar.Name.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pd))
                            {
                                int expectedCount = 2; // Default for 64-bit float pair (e.g. Vector2)
                                if (ctx.TypeModel != null)
                                {
                                    var layout = FindLayoutByShortName(ctx.TypeModel, boxType);
                                    if (layout != null && layout.InstanceFieldCount > 0 && layout.InstanceFieldCount <= 2)
                                    {
                                        expectedCount = layout.InstanceFieldCount;
                                    }
                                }

                                if (expectedCount == 2)
                                {
                                    long raw = BitConverter.DoubleToInt64Bits(pd);
                                    float f0 = BitConverter.Int32BitsToSingle((int)(raw & 0xFFFFFFFF));
                                    float f1 = BitConverter.Int32BitsToSingle((int)(raw >> 32));
                                    components.Add(new ExprLiteral(f0));
                                    components.Add(new ExprLiteral(f1));
                                }
                                else
                                {
                                    components.Add(value);
                                }
                            }

                            else if (value is ExprStructInit si)
                            {
                                // Structural: decompose struct init fields directly
                                foreach (var (_, fieldValue) in si.Fields)
                                {
                                    var valCopy = fieldValue;
                                    if (value.StaticFieldHint != null)
                                        valCopy.StaticFieldHint = value.StaticFieldHint;
                                    components.Add(valCopy);
                                }
                            }
                            else
                                components.Add(value);
                        }

                        // Cap the number of components to the actual number of fields in the struct
                        if (ctx.TypeModel != null)
                        {
                            var layout = FindLayoutByShortName(ctx.TypeModel, boxType);
                            if (layout != null && layout.InstanceFieldCount > 0 && components.Count > layout.InstanceFieldCount)
                            {
                                components = components.Take(layout.InstanceFieldCount).ToList();
                            }
                        }

                        if (isEnum && components.Count == 1)
                        {
                            var enumValue = CoerceEnumBoxValue(components[0], boxType, inst, ctx);
                            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → enum box: {enumValue.Emit()}");
                            return enumValue;
                        }

                        // Check for static field hint: if all components share the same hint,
                        // emit the static field reference directly
                        var hints = components.Select(c => c.StaticFieldHint).Where(h => h != null).Distinct().ToList();
                        if (hints.Count == 1)
                        {
                            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → static field hint: {hints[0]}");
                            return new ExprVar(hints[0]!);
                        }

                        // Check for uniform field access: if all components are ExprField with the same target,
                        // and the target is the same expression, emit the common field accessor
                        if (components.Count > 0 && components.All(c => c is ExprField))
                        {
                            var fieldTargets = components.Cast<ExprField>()
                                .Select(f => f.Target)
                                .ToList();

                            // Check if all targets are the same variable
                            if (fieldTargets.All(t => t is ExprVar) &&
                                fieldTargets.Cast<ExprVar>().Select(v => v.Name).Distinct().Count() == 1)
                            {
                                var distinctValues = components.Cast<ExprField>()
                                    .Select(f => f.FieldName).Distinct().Count();

                                // All same field access on same base → just the base var
                                if (distinctValues == 1)
                                {
                                    var baseExpr = fieldTargets[0];
                                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → uniform component: {baseExpr.Emit()}");
                                    return baseExpr;
                                }

                                // Different fields on same base → redundant struct copy
                                string baseVarName = ((ExprVar)fieldTargets[0]).Name;
                                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → redundant struct copy: {baseVarName}");
                                return new ExprVar(baseVarName);
                            }
                        }

                        // Default: construct new T(arg1, arg2, ...)
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → new {boxType}({string.Join(", ", components.Select(c => c.Emit()))})");
                        return new ExprNew(boxType, args: components);
                    }
                }
            }
            else
            {
                // For 64-bit types, the StoreBuilder may have split the 64-bit store into two 32-bit SP stores.
                bool is64Bit = boxType is "double" or "Double" or "long" or "Int64" or "ulong" or "UInt64";
                if (is64Bit && currentStmts.Count >= 2)
                {
                    for (int si = currentStmts.Count - 1; si >= Math.Max(1, currentStmts.Count - 4); si--)
                    {
                        if (currentStmts[si].Expr is ExprAssign upperAssign && upperAssign.Target is ExprSpSlot upperSp &&
                            currentStmts[si - 1].Expr is ExprAssign lowerAssign && lowerAssign.Target is ExprSpSlot lowerSp &&
                            upperSp.Offset == lowerSp.Offset + 4)
                        {
                            if (upperAssign.Value is ExprLiteral upperLit && lowerAssign.Value is ExprLiteral lowerLit &&
                                upperLit.Value is int u && lowerLit.Value is int l)
                            {
                                currentStmts.RemoveAt(si);
                                currentStmts.RemoveAt(si - 1);

                                long combinedRaw = (uint)l | ((long)(uint)u << 32);
                                ExprNode boxedValue = new ExprLiteral(combinedRaw);
                                boxedValue = BoxingValueNormalizer.Normalize(boxType, boxedValue);

                                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → unboxed combined 64-bit: {boxedValue.Emit()}");
                                return boxedValue;
                            }
                        }
                    }
                }

                for (int si = currentStmts.Count - 1; si >= Math.Max(0, currentStmts.Count - 4); si--)
                {
                    if (currentStmts[si].Expr is ExprAssign assign && assign.Target is ExprSpSlot)
                    {
                        var boxedValue = assign.Value;
                        currentStmts.RemoveAt(si);

                        boxedValue = BoxingValueNormalizer.Normalize(boxType, boxedValue);

                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → unboxed value: {boxedValue.Emit()}");
                        return boxedValue;
                    }
                }
            }
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          → fallback box<{boxType}>");
        return new ExprCall($"box<{boxType}>");
    }

    /// <summary>
    /// Find a FieldLayout by matching the short type name (e.g., "Vector3" matches "UnityEngine.Vector3").
    /// Metadata-driven — no string matching on expression output.
    /// </summary>
    private static Rosetta.Model.FieldLayout? FindLayoutByShortName(Rosetta.Model.TypeModel typeModel, string shortName)
    {
        // Try exact match first (O(1) dictionary lookup)
        if (typeModel.FieldLayoutsByTypeName.TryGetValue(shortName, out int exactIdx))
        {
            if (typeModel.FieldLayouts.TryGetValue(exactIdx, out var exactLayout))
                return exactLayout;
        }

        // Fallback: scan for suffix match (handles stripped namespaces)
        foreach (var layout in typeModel.FieldLayouts.Values)
        {
            string layoutName = layout.TypeName;
            if (layoutName == shortName)
                return layout;

            // Check if layoutName ends with ".shortName"
            int expectedDotPos = layoutName.Length - shortName.Length - 1;
            if (expectedDotPos >= 0 &&
                layoutName.Length > shortName.Length &&
                layoutName[expectedDotPos] == '.' &&
                layoutName.AsSpan(expectedDotPos + 1).SequenceEqual(shortName.AsSpan()))
            {
                return layout;
            }
        }
        return null;
    }

    private static ExprNode CoerceEnumBoxValue(ExprNode value, string enumType, IrInstruction inst, PropagationContext ctx)
    {
        if (inst.BoxedTypeDefIndex >= 0 &&
            TryGetIntegralLiteral(value, out long rawValue))
        {
            string? enumLiteral = ctx.TypeModel?.ResolveEnumLiteralByTypeDefIndex(inst.BoxedTypeDefIndex, rawValue);
            if (enumLiteral != null)
                return new ExprVar(enumLiteral) { InferredType = enumType };
        }

        value.InferredType ??= enumType;
        return value;
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
