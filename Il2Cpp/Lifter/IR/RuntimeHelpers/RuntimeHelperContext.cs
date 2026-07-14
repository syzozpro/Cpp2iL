using System;
using System.Collections.Generic;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Analysis.Resolve;
using Rosetta.Binary;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Metadata;
using Rosetta.Model;

namespace Rosetta.Lifter.IR.RuntimeHelpers;

public sealed class RuntimeHelperContext
{
    public List<IrInstruction> Insts { get; }
    public DefUseIndex DuIndex { get; }
    public int Index { get; }
    public IrInstruction Inst { get; }
    
    public IrInstruction? X0Def { get; }
    public IrInstruction? X1Def { get; }
    public int X0DefIdx { get; }
    
    public IrInstruction? EffectiveX0Def { get; }
    public int EffectiveX0DefIdx { get; }
    
    public GlobalAddressMap? AddressMap { get; }
    public TypeModel? TypeModel { get; }
    public MetadataParser? Metadata { get; }
    public TypeResolver? TypeResolver { get; }
    
    public RuntimeHelperContext(
        List<IrInstruction> insts,
        DefUseIndex duIndex,
        int index,
        IrInstruction inst,
        IrInstruction? x0Def,
        IrInstruction? x1Def,
        int x0DefIdx,
        IrInstruction? effectiveX0Def,
        int effectiveX0DefIdx,
        GlobalAddressMap? addressMap,
        TypeModel? typeModel,
        MetadataParser? metadata,
        TypeResolver? typeResolver)
    {
        Insts = insts;
        DuIndex = duIndex;
        Index = index;
        Inst = inst;
        X0Def = x0Def;
        X1Def = x1Def;
        X0DefIdx = x0DefIdx;
        EffectiveX0Def = effectiveX0Def;
        EffectiveX0DefIdx = effectiveX0DefIdx;
        AddressMap = addressMap;
        TypeModel = typeModel;
        Metadata = metadata;
        TypeResolver = typeResolver;
    }

    public string ResolveBoxedTypeName(int loadIdx, long typeOffset)
    {
        if (loadIdx < 0) return "?";
        var loadInst = Insts[loadIdx];

        if (loadInst.Sources.Length > 0 && loadInst.Sources[0].Kind == IrOperandKind.Memory)
        {
            long baseReg = loadInst.Sources[0].Value;
            var (prev, _) = IrTracingUtils.FindDefinition(Insts, loadIdx, baseReg, loadIdx);
            if (prev != null)
            {
                if (prev.Annotation != null)
                {
                    if (prev.Annotation.Contains("typeof("))
                        return IrDataResolver.ExtractTypeName(prev.Annotation);
                }
            }
        }

        if (AddressMap != null && AddressMap.Il2CppDefaultsLayout.TryGetValue((uint)typeOffset, out var dynamicType))
        {
            return Rosetta.Analysis.Utils.TypeAliasUtils.GetCSharpAlias(dynamicType);
        }
        
        return $"type_0x{typeOffset:X}";
    }

    public int ResolveBoxedTypeDefIndex(int loadIdx)
    {
        if (TypeModel == null || loadIdx < 0) return -1;
        var loadInst = Insts[loadIdx];
        if (loadInst.Sources.Length == 0 || loadInst.Sources[0].Kind != IrOperandKind.Memory)
            return -1;

        long baseReg = loadInst.Sources[0].Value;
        var (prev, _) = IrTracingUtils.FindDefinition(Insts, loadIdx, baseReg, loadIdx);
        if (prev != null)
        {
            if (prev.MetadataIndex >= 0 &&
                prev.MetadataKind is Rosetta.Analysis.Resolve.AddressKind.RuntimeClass
                    or Rosetta.Analysis.Resolve.AddressKind.RuntimeType)
            {
                return TypeModel.ResolveTypeDefIndexFromTypeIndex(prev.MetadataIndex);
            }
        }

        return -1;
    }

    public string? FindAddrAnnotationForLoad(int loadIdx)
    {
        if (loadIdx < 0 || loadIdx >= Insts.Count) return null;
        var loadInst = Insts[loadIdx];
        if (loadInst.Sources.Length == 0) return null;

        if (loadInst.Annotation != null) return loadInst.Annotation;

        if (loadInst.Sources[0].Kind == IrOperandKind.Memory)
        {
            long baseReg = loadInst.Sources[0].Value;
            var (prev, _) = IrTracingUtils.FindDefinitionSatisfying(
                Insts, loadIdx, baseReg, inst => inst.Annotation != null, 1024);
            if (prev != null)
                return prev.Annotation;
        }
        return null;
    }

    public string? ExtractMultiDimBounds(int callIdx, long spOffset)
    {
        for (int k = callIdx - 1; k >= Math.Max(0, callIdx - 12); k--)
        {
            var prev = Insts[k];
            if (prev.Opcode != IrOpcode.Store) continue;
            if (prev.Sources.Length < 2) continue;
            var storeDst = prev.Sources[0];
            if (storeDst.Kind != IrOperandKind.Memory || !IrRegisterConstants.IsStackPointer(storeDst.Value)) continue;
            if (storeDst.Offset != spOffset) continue;

            var storeSrc = prev.Sources[1];
            if (storeSrc.Kind != IrOperandKind.FpRegister) continue;

            long fpReg = storeSrc.Value;
            var (fpDef, _) = IrTracingUtils.FindDefinition(Insts, k, fpReg, 8, IrOperandKind.FpRegister);
            if (fpDef != null)
            {
                string? ann = fpDef.Annotation;
                if (ann != null && ann.StartsWith("packed4("))
                {
                    string inner = ann["packed4(".Length..^1];
                    string[] parts = inner.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 4)
                    {
                        var lengths = new List<int>();
                        for (int p = 0; p < parts.Length; p += 2)
                        {
                            if (float.TryParse(parts[p], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float fVal))
                            {
                                int dim = BitConverter.ToInt32(BitConverter.GetBytes(fVal), 0);
                                if (dim > 0 && dim <= 65536)
                                    lengths.Add(dim);
                            }
                        }

                        if (lengths.Count > 0)
                            return "[" + string.Join(", ", lengths) + "]";
                    }
                }
            }
            break;
        }
        return null;
    }
}
