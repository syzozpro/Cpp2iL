using System;
using System.Collections.Generic;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Analysis.Utils;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

public sealed partial class ExprPropagator
{
    private ExprNode BuildStoreTarget(IrInstruction inst, IrOperand dst, IrOperand val)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildStoreTarget called! dst.Kind={dst.Kind}, val.BitWidth={val.BitWidth}");
        if (inst.Annotation != null && !Rosetta.Analysis.Utils.StringUtils.IsArrayElementAnnotation(inst.Annotation))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildStoreTarget -> routing to BuildAnnotatedStoreTarget due to annotation: {inst.Annotation}");
            return BuildAnnotatedStoreTarget(inst, dst);
        }

        if (dst.Kind == IrOperandKind.Memory && ArmUtils.IsStackPointer(dst.Value))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        target: SP slot 0x{dst.Offset:X}");
            return new ExprSpSlot(dst.Offset);
        }

        if (dst.Kind == IrOperandKind.Memory && dst.Value == 29 && _ctx.FpSpOffset >= 0)
        {
            long effectiveSpOffset = _ctx.FpSpOffset + dst.Offset;
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        target: FP-relative → SP slot 0x{effectiveSpOffset:X} (x29 + {dst.Offset}, fpOff={_ctx.FpSpOffset})");
            return new ExprSpSlot(effectiveSpOffset);
        }

        if (dst.Kind == IrOperandKind.Memory)
            return BuildMemoryStoreTarget(inst, dst, val);

        var target = GetSourceExpr(inst, 0);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        target: source[0] {target.Emit()}");
        return target;
    }

    private ExprNode BuildAnnotatedStoreTarget(IrInstruction inst, IrOperand dst)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildAnnotatedStoreTarget started for annotation: {inst.Annotation}");
        string annotation = inst.Annotation!;
        ExprNode target;
        if (annotation.StartsWith("->"))
        {
            var baseExpr = GetRegExpr(dst.Value, inst.Address, 0);
            target = new ExprField(baseExpr, Rosetta.Analysis.Utils.StringUtils.CleanFieldName(annotation[2..], _typeModel));
        }
        else
        {
            target = new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanFieldName(annotation, _typeModel));
        }

        if (ConsoleReporter.IsTracing)
            ConsoleReporter.Debug($"        target: annotated field \"{Rosetta.Analysis.Utils.StringUtils.CleanFieldName(annotation, _typeModel)}\"");
        return target;
    }

    private ExprNode BuildMemoryStoreTarget(IrInstruction inst, IrOperand dst, IrOperand val)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] BuildMemoryStoreTarget started for dst.Value={dst.Value}, dst.Offset=0x{dst.Offset:X}");
        var baseExpr = GetRegExpr(dst.Value, inst.Address, 0);

        if (dst.Offset == 0 && inst.Sources.Length > 2 && inst.Sources[2].Kind == IrOperandKind.Register)
        {
            var indexExpr = GetSourceExpr(inst, 2);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → register-indexed store: {baseExpr.Emit()}[{indexExpr.Emit()}] = {val}");
            return new ExprIndex(ArrayAddressResolver.StripArrayDataOffset(baseExpr), indexExpr);
        }

        var target = ParameterFieldResolver.TryResolve(baseExpr, (int)dst.Value, dst.Offset, _method.GpParamTypeMap, _typeModel)
            ?? (TryMakeArrayAccess(baseExpr, dst.Offset, val.BitWidth)
              ?? TryMakeMultiDimAccess(baseExpr, dst.Offset)
              ?? (ExprNode)new ExprMemory(baseExpr, dst.Offset));

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        target: memory {target.Emit()}");
        return target;
    }

    private static uint[]? TryExtract4Slots(ExprNode value)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] TryExtract4Slots started for value type: {value.GetType().Name}");
        if (value is ExprSimd simd)
        {
            return simd.GetSlots32();
        }
        return null;
    }

    private ExprNode? SplitMerged64(string[] annParts, long rawVal, ExprNode target,
        string? mainType, IrOperand dst, IrInstruction inst)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        [DEBUG] SplitMerged64 started! parts={annParts.Length}, rawVal=0x{rawVal:X16}");
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        SplitMerged64: {annParts.Length} parts, raw=0x{rawVal:X16}");

        if (!BlockStatements.TryGetValue(_ctx.CurrentBlockId, out var stmts))
            return new ExprAssign(target, new ExprLiteral(rawVal));

        int bitPos = 0;
        ExprNode? lastAssign = null;
        for (int p = 0; p < annParts.Length; p++)
        {
            string? pType = Rosetta.Analysis.Utils.StringUtils.ExtractTypeHint(annParts[p]);
            int fieldBits = TypeUtils.GetFieldBitSize(pType);
            
            ExprNode pTarget;
            if (annParts[p].StartsWith("->") || annParts[p].StartsWith("."))
            {
                var baseE = GetRegExpr(dst.Value, inst.Address, 0);
                string fieldStr = annParts[p].StartsWith("->") ? annParts[p][2..] : annParts[p][1..];
                pTarget = new ExprField(baseE, Rosetta.Analysis.Utils.StringUtils.CleanFieldName(fieldStr, _typeModel));
            }
            else
            {
                pTarget = new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanFieldName(annParts[p], _typeModel));
            }

            ulong mask = fieldBits == 64 ? ulong.MaxValue : ((1UL << fieldBits) - 1);
            ulong fieldRaw = ((ulong)rawVal >> bitPos) & mask;
            
            var elementType = TypeUtils.TypeHintToElementType(pType);
            ExprNode pVal;
            if (elementType != Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_END)
            {
                int fieldSize = TypeUtils.ElementTypeBitSize(elementType) / 8;
                pVal = BitUtils.ExtractFieldFromRaw((long)fieldRaw, 0, fieldSize, elementType);
            }
            else
            {
                pVal = fieldBits <= 32
                    ? BitUtils.Decode32BitValue((uint)fieldRaw, pType)
                    : new ExprLiteral((long)fieldRaw);
            }

            var assign = new ExprAssign(pTarget, pVal);
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          merge64[{p}]: {pTarget.Emit()} = {pVal.Emit()} (type={pType ?? "null"}, bits={fieldBits}, bitPos={bitPos})");

            if (p < annParts.Length - 1)
                stmts.Add(new ExprStatement { Expr = assign });
            else
                lastAssign = assign;

            bitPos += fieldBits;
        }
        return lastAssign;
    }

    private bool TryHandleInlinedArrayInitAndMemcpy(ExprNode target, ExprNode value)
    {
        // ══════════════════════════════════════════════════════════════
        // V104+ inlined array init: arr[0] = field(<PrivateImpl>.HASH)
        // ══════════════════════════════════════════════════════════════
        if (target is ExprIndex arrIdx && arrIdx.Index is ExprLiteral idxLit &&
            idxLit.Value is int idxVal && idxVal == 0)
        {
            string? fieldLabel = ArrayLiteralRecovery.TryExtractFieldLabel(value);
            if (fieldLabel != null && fieldLabel.Contains("PrivateImplementationDetails"))
            {
                string arrayVarName = arrIdx.Target is ExprVar arrVar ? arrVar.Name : arrIdx.Target.Emit();
                if (ArrayLiteralRecovery.TryRecoverInlinedArrayInit(value, arrayVarName, _ssa, ExprMap, _ctx, _fieldRvaResolver, out var recoveredLabel))
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → inlined array init recovered, suppressing store");
                    if (recoveredLabel != null) _ctx.SuppressedFieldLabels.Add(recoveredLabel);
                    return true; // suppress the store
                }
            }
        }

        // Suppress memcpy continuation stores
        if (_ctx.SuppressedFieldLabels.Count > 0)
        {
            string? valFieldLabel = ArrayLiteralRecovery.TryExtractFieldLabel(value);
            if (valFieldLabel != null)
            {
                foreach (var label in _ctx.SuppressedFieldLabels)
                {
                    if (valFieldLabel.Contains(label))
                    {
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → suppressing memcpy continuation for {label}");
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private bool TryHandleQRegisterStore(
        IrInstruction inst, IrOperand dst, IrOperand val, ExprNode target, ExprNode value, 
        out ExprNode? result)
    {
        result = null;
        if (inst.Annotation == null || dst.BitWidth != 128 || dst.Kind != IrOperandKind.Memory)
            return false;

        // ══════════════════════════════════════════════════════════════
        // 128-bit Q-register store: type-aware multi-field splitting
        // ══════════════════════════════════════════════════════════════
        if (inst.Annotation.Contains('+'))
        {
            string[] annParts = inst.Annotation.Split('+');
            uint[]? slots = TryExtract4Slots(value);

            if (slots != null && annParts.Length >= 2 &&
                BlockStatements.TryGetValue(_ctx.CurrentBlockId, out var qStmts))
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        Q-register multi-field split: {annParts.Length} fields, slots=[{slots[0]},{slots[1]},{slots[2]},{slots[3]}]");

                string? firstType = Rosetta.Analysis.Utils.StringUtils.ExtractTypeHint(annParts[0]);
                if (firstType is "long" or "ulong" && annParts.Length == 2)
                {
                    long lower64 = (long)slots[0] | ((long)slots[1] << 32);
                    long upper64 = (long)slots[2] | ((long)slots[3] << 32);

                    ExprNode firstTarget = new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanFieldName(annParts[0], _typeModel));
                    ExprNode firstVal = firstType == "long"
                        ? new ExprLiteral(lower64)
                        : (ExprNode)new ExprLiteral((ulong)lower64);

                    string? secondType = Rosetta.Analysis.Utils.StringUtils.ExtractTypeHint(annParts[1]);
                    ExprNode secondTarget = new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanFieldName(annParts[1], _typeModel));
                    ExprNode secondVal = secondType == "ulong"
                        ? new ExprLiteral((ulong)upper64)
                        : (ExprNode)new ExprLiteral(upper64);

                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          pair64[0]: {firstTarget.Emit()} = {firstVal.Emit()}");
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          pair64[1]: {secondTarget.Emit()} = {secondVal.Emit()}");
                    qStmts.Add(new ExprStatement { Expr = new ExprAssign(firstTarget, firstVal) });
                    result = new ExprAssign(secondTarget, secondVal);
                    return true;
                }

                int count = Math.Min(annParts.Length, 4);
                ExprNode? lastAssign = null;
                for (int p = 0; p < count; p++)
                {
                    string? pType = Rosetta.Analysis.Utils.StringUtils.ExtractTypeHint(annParts[p]);
                    ExprNode pTarget = new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanFieldName(annParts[p], _typeModel));
                    ExprNode pVal = BitUtils.Decode32BitValue(slots[p], pType);
                    var assign = new ExprAssign(pTarget, pVal);
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          Q[{p}]: {pTarget.Emit()} = {pVal.Emit()} (type={pType ?? "null"})");
                    if (p < count - 1)
                        qStmts.Add(new ExprStatement { Expr = assign });
                    else
                        lastAssign = assign;
                }
                result = lastAssign;
                return true;
            }
        }
        // ══════════════════════════════════════════════════════════════
        // 128-bit Q-register store: struct decomposition (No + in annotation)
        // ══════════════════════════════════════════════════════════════
        else if (value is ExprSimd simdVal)
        {
            string? typeHint = Rosetta.Analysis.Utils.StringUtils.ExtractTypeHint(inst.Annotation);
            if (typeHint != null)
            {
                var layout = _typeModel?.GetLayoutForTypeName(typeHint);
                if (layout != null && layout.InstanceFieldCount > 0)
                {
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        Q-register struct decomposition: {typeHint}, {layout.InstanceFieldCount} fields");
                    
                    byte[] buffer = new byte[16];
                    System.Buffer.BlockCopy(System.BitConverter.GetBytes(simdVal.RawLo), 0, buffer, 0, 8);
                    System.Buffer.BlockCopy(System.BitConverter.GetBytes(simdVal.RawHi), 0, buffer, 8, 8);
                    
                    var structInit = new ExprStructInit(typeHint);
                    
                    var sortedFields = new List<Rosetta.Model.FieldLayout.FieldEntry>();
                    foreach (var f in layout.Fields) if (!f.IsStatic) sortedFields.Add(f);
                    sortedFields.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                    
                    foreach (var field in sortedFields)
                    {
                        int relativeOffset = field.Offset - sortedFields[0].Offset;
                        
                        if (relativeOffset >= 16) break;
                        
                        int requiredBytes = field.ElementType switch
                        {
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_R8 or Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I8 or Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_U8 => 8,
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I2 or Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_U2 or Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_CHAR => 2,
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I1 or Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_U1 or Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN => 1,
                            _ => 4
                        };
                        
                        if (relativeOffset + requiredBytes > 16) continue;
                        
                        ExprNode fieldVal = field.ElementType switch
                        {
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_R4 => new ExprLiteral(System.BitConverter.ToSingle(buffer, relativeOffset)),
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_R8 => new ExprLiteral(System.BitConverter.ToDouble(buffer, relativeOffset)),
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I8 => new ExprLiteral(System.BitConverter.ToInt64(buffer, relativeOffset)),
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_U8 => new ExprLiteral(System.BitConverter.ToUInt64(buffer, relativeOffset)),
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I2 => new ExprLiteral(System.BitConverter.ToInt16(buffer, relativeOffset)),
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_U2 => new ExprLiteral(System.BitConverter.ToUInt16(buffer, relativeOffset)),
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_I1 => new ExprLiteral((sbyte)buffer[relativeOffset]),
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_U1 => new ExprLiteral(buffer[relativeOffset]),
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN => new ExprLiteral(buffer[relativeOffset] != 0),
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_CHAR => BitUtils.MakeCharLiteral((char)System.BitConverter.ToInt16(buffer, relativeOffset)),
                            Rosetta.Common.Il2CppTypeEnum.IL2CPP_TYPE_U4 => new ExprLiteral(System.BitConverter.ToUInt32(buffer, relativeOffset)),
                            _ => new ExprLiteral(System.BitConverter.ToInt32(buffer, relativeOffset))
                        };
                            
                        structInit.Fields.Add((field.Name, fieldVal));
                        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          field {field.Name}: struct_offset 0x{relativeOffset:X} = {fieldVal.Emit()} (type={field.ElementType})");
                    }
                    
                    result = new ExprAssign(target, structInit);
                    return true;
                }
            }
        }
        return false;
    }

    private bool TryHandleMerged64Store(
        IrInstruction inst, IrOperand dst, IrOperand val, ExprNode target, ExprNode value, 
        out ExprNode? result)
    {
        result = null;
        bool hasMergeContext = inst.Annotation != null
            || (dst.Kind == IrOperandKind.Memory && !ArmUtils.IsStackPointer(dst.Value) && dst.Value != 29
                && _method.GpParamTypeMap.ContainsKey((int)dst.Value));
        long? rawValOpt = (hasMergeContext && val.BitWidth == 64 && dst.Kind == IrOperandKind.Memory)
            ? BitUtils.ExtractRawBits64(value) : null;

        if (!rawValOpt.HasValue || rawValOpt.Value <= 0xFFFFFFFF)
            return false;

        long rawVal = rawValOpt.Value;
        string? mainType = inst.Annotation != null ? Rosetta.Analysis.Utils.StringUtils.ExtractTypeHint(inst.Annotation) : null;
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        merged store detected: raw=0x{rawVal:X16} mainType={mainType ?? "null"}");

        // Single 64-bit field
        if (mainType is "double" or "long" or "ulong" or "decimal")
        {
            ExprNode mainVal = mainType switch
            {
                "double" => new ExprLiteral(BitConverter.ToDouble(BitConverter.GetBytes(rawVal))),
                "long" => new ExprLiteral(rawVal),
                "ulong" => new ExprLiteral((ulong)rawVal),
                _ => new ExprLiteral(rawVal)
            };
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → 64-bit {mainType}: {target.Emit()} = {mainVal.Emit()}");
            result = new ExprAssign(target, mainVal);
            return true;
        }

        // Multi-field split
        if (inst.Annotation != null && inst.Annotation.Contains('+'))
        {
            string[] annParts = inst.Annotation.Split('+');
            result = SplitMerged64(annParts, rawVal, target, mainType, dst, inst);
            return true;
        }

        uint lower32 = (uint)(rawVal & 0xFFFFFFFF);
        uint upper32 = (uint)((rawVal >> 32) & 0xFFFFFFFF);
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → split: lower=0x{lower32:X8} upper=0x{upper32:X8}");

        int regNum = (int)dst.Value;
        if (_method.GpParamIsOut.Contains(regNum)
            && _method.GpParamTypeMap.TryGetValue(regNum, out string? outTypeName)
            && _typeModel != null
            && _typeModel.FieldLayoutsByTypeName.TryGetValue(outTypeName, out int outTypeDefIdx))
        {
            var baseExpr = GetRegExpr(dst.Value, inst.Address, 0);
            var structInit = new ExprStructInit(Rosetta.Analysis.Utils.StringUtils.CleanTypeName(outTypeName));

            const int ObjectHeaderSize = 16;
            if (_typeModel.FieldLayouts.TryGetValue(outTypeDefIdx, out var layout))
            {
                int instanceIdx = 0;
                foreach (var field in layout.Fields)
                {
                    if (field.IsStatic) continue;
                    long fieldStackOffset = field.Offset - ObjectHeaderSize;

                    long posInStore = fieldStackOffset - dst.Offset;
                    if (posInStore < 0 || posInStore >= 8) { instanceIdx++; continue; }

                    int fieldSize = layout.GetInstanceFieldSize(instanceIdx);
                    if (fieldSize <= 0) fieldSize = (int)(8 - posInStore);

                    ExprNode fieldVal = BitUtils.ExtractFieldFromRaw(rawVal, (int)posInStore, fieldSize, field.ElementType);
                    structInit.Fields.Add((field.Name, fieldVal));
                    instanceIdx++;
                }
            }

            if (structInit.Fields.Count > 0)
            {
                result = new ExprAssign(baseExpr, structInit);
                return true;
            }
        }

        if (_typeModel != null && !ArmUtils.IsStackPointer(dst.Value) && dst.Value != 29
            && _method.GpParamTypeMap.TryGetValue((int)dst.Value, out string? refTypeName)
            && _typeModel.FieldLayoutsByTypeName.TryGetValue(refTypeName, out int refTypeDefIdx)
            && _typeModel.FieldLayouts.TryGetValue(refTypeDefIdx, out var refLayout)
            && BlockStatements.TryGetValue(_ctx.CurrentBlockId, out var stmts))
        {
            const int ObjHeader = 16;
            ExprNode? lastAssign = null;
            var baseE = GetRegExpr(dst.Value, inst.Address, 0);

            int refInstanceIdx = 0;
            foreach (var field in refLayout.Fields)
            {
                if (field.IsStatic) continue;
                long fieldStackOff = field.Offset - ObjHeader;
                long posInStore = fieldStackOff - dst.Offset;
                if (posInStore < 0 || posInStore >= 8) { refInstanceIdx++; continue; }

                int fSize = refLayout.GetInstanceFieldSize(refInstanceIdx);
                if (fSize <= 0) fSize = (int)(8 - posInStore);

                ExprNode fTarget = ParameterFieldResolver.TryResolve(baseE, (int)dst.Value, fieldStackOff, _method.GpParamTypeMap, _typeModel)
                                ?? (ExprNode)new ExprMemory(baseE, fieldStackOff);
                ExprNode fVal = BitUtils.ExtractFieldFromRaw(rawVal, (int)posInStore, fSize, field.ElementType);
                var assign = new ExprAssign(fTarget, fVal);

                if (lastAssign != null)
                    stmts.Add(new ExprStatement { Expr = lastAssign });
                lastAssign = assign;
                refInstanceIdx++;
            }

            if (lastAssign != null)
            {
                result = lastAssign;
                return true;
            }
        }

        // Annotation-based fallback
        {
            ExprNode firstTarget = target;
            ExprNode lowerVal = BitUtils.Decode32BitValue(lower32, mainType);
            var mainAssign = new ExprAssign(firstTarget, lowerVal);

            if (BlockStatements.TryGetValue(_ctx.CurrentBlockId, out var stmts2))
            {
                long adjOffset = dst.Offset + 4;
                ExprNode? adjTarget = null;
                string? adjType = null;

                int plusIdx = inst.Annotation?.IndexOf('+') ?? -1;
                if (plusIdx > 0)
                {
                    string adjPart = inst.Annotation![(plusIdx + 1)..];
                    adjTarget = new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanFieldName(adjPart, _typeModel));
                    adjType = Rosetta.Analysis.Utils.StringUtils.ExtractTypeHint(adjPart);
                }

                if (adjTarget == null)
                {
                    long baseReg = dst.Value;
                    if (_ctx.MemAnnotations.TryGetValue((baseReg, adjOffset), out var ann))
                    {
                        adjTarget = new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanFieldName(ann, _typeModel));
                        adjType = Rosetta.Analysis.Utils.StringUtils.ExtractTypeHint(ann);
                    }
                }

                adjTarget ??= new ExprMemory(GetRegExpr(dst.Value, inst.Address, 0), adjOffset);

                ExprNode upperVal = BitUtils.Decode32BitValue(upper32, adjType);
                stmts2.Add(new ExprStatement { Expr = mainAssign });
                result = new ExprAssign(adjTarget, upperVal);
                return true;
            }

            result = mainAssign;
            return true;
        }
    }

    private bool TryHandleQRegisterSimdPackedStore(
        IrInstruction inst, IrOperand dst, IrOperand val, ExprNode value, 
        out ExprNode? result)
    {
        result = null;
        if (inst.Annotation != null && dst.BitWidth == 128 && dst.Kind == IrOperandKind.Memory &&
            value is ExprVar evSingle && evSingle.Name.StartsWith("packed4("))
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        Q-register SIMD split: packed4 value (single field)");
            string[] parts = evSingle.Name[8..^1].Split(", ");
            if (parts.Length == 4 && BlockStatements.TryGetValue(_ctx.CurrentBlockId, out var stmts))
            {
                string[] annParts = inst.Annotation.Split('+');
                for (int p = 0; p < 4; p++)
                {
                    string pVal = parts[p];
                    string? pAnn = p < annParts.Length ? annParts[p] : null;
                    ExprNode pTarget = pAnn != null 
                        ? new ExprVar(Rosetta.Analysis.Utils.StringUtils.CleanFieldName(pAnn, _typeModel)) 
                        : new ExprMemory(GetRegExpr(dst.Value, inst.Address, 0), dst.Offset + p * 4);
                    var assign = new ExprAssign(pTarget, new ExprLiteral(pVal + "f"));
                    if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"          Q[{p}]: {pTarget.Emit()} = {pVal}f");
                    if (p < 3) stmts.Add(new ExprStatement { Expr = assign });
                    else
                    {
                        result = assign;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private bool TryHandleUnannotated64SpStore(
        IrInstruction inst, IrOperand dst, IrOperand val, ExprNode value, 
        out ExprNode? result)
    {
        result = null;
        if (dst.Kind == IrOperandKind.Memory && (ArmUtils.IsStackPointer(dst.Value) || dst.Value == 29) && val.BitWidth == 64 && inst.Annotation == null)
        {
            long? spRawValOpt = BitUtils.ExtractRawBits64(value);
            if (spRawValOpt.HasValue && spRawValOpt.Value > 0xFFFFFFFF)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"        Unannotated 64-bit [SP] split: raw=0x{spRawValOpt.Value:X16}");
                if (BlockStatements.TryGetValue(_ctx.CurrentBlockId, out var stmts))
                {
                    uint lower32 = (uint)(spRawValOpt.Value & 0xFFFFFFFF);
                    uint upper32 = (uint)((spRawValOpt.Value >> 32) & 0xFFFFFFFF);

                    long effectiveOffset = (dst.Value == 29 && _ctx.FpSpOffset >= 0) ? _ctx.FpSpOffset + dst.Offset : dst.Offset;

                    ExprNode targetLower = new ExprSpSlot(effectiveOffset);
                    ExprNode targetUpper = new ExprSpSlot(effectiveOffset + 4);

                    var lowerAssign = new ExprAssign(targetLower, new ExprLiteral((int)lower32));
                    stmts.Add(new ExprStatement { Expr = lowerAssign });
                    result = new ExprAssign(targetUpper, new ExprLiteral((int)upper32));
                    return true;
                }
            }
        }
        return false;
    }

    private ExprNode CoerceStoreValue(ExprNode target, ExprNode value, string? annotation)
    {
        if (annotation != null && value is ExprLiteral singleLit)
        {
            string? fieldType = Rosetta.Analysis.Utils.StringUtils.ExtractTypeHint(annotation);
            if (fieldType is "bool" && singleLit.Value is int boolInt)
            {
                value = new ExprLiteral(boolInt != 0);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → type-hint bool: {boolInt} → {value.Emit()}");
            }
            else if (fieldType is "bool" && singleLit.Value is long boolLong)
            {
                value = new ExprLiteral(boolLong != 0);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → type-hint bool: {boolLong} → {value.Emit()}");
            }
            else if (fieldType is "char" && singleLit.Value is int charInt)
            {
                value = BitUtils.Decode32BitValue((uint)charInt, "char");
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → type-hint char: {charInt} → {value.Emit()}");
            }
            else if (fieldType is "char" && singleLit.Value is long charLong)
            {
                value = BitUtils.Decode32BitValue((uint)charLong, "char");
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → type-hint char: {charLong} → {value.Emit()}");
            }
        }

        if (target is ExprIndex idxTarget && value is ExprLiteral boolLiteral)
        {
            string? arrType = idxTarget.Target.InferredType;
            if (arrType == null && idxTarget.Target is ExprNew arrNew)
                arrType = arrNew.TypeName + "[]";
            if (arrType == "bool[]" && boolLiteral.Value is int bi)
            {
                value = new ExprLiteral(bi != 0);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → bool[] coercion: {bi} → {value.Emit()}");
            }
            else if (arrType == "bool[]" && boolLiteral.Value is long bl)
            {
                value = new ExprLiteral(bl != 0);
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        → bool[] coercion: {bl} → {value.Emit()}");
            }
        }
        return value;
    }
}