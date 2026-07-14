using System;
using System.Collections.Generic;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Analysis.Utils;
using static Rosetta.Analysis.Utils.TypeAliasUtils;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR.FieldAnnotation;

internal sealed class FieldAnnotationContext
{
    public List<IrInstruction> Insts { get; }
    public DefUseIndex DuIndex { get; }
    public IrMethod Method { get; }
    public int Index { get; }
    public IrInstruction Inst { get; }
    
    public long Offset { get; }
    public long BaseReg { get; }
    
    public Dictionary<long, IrDataResolver.ObjectAlias> ObjectAliases { get; }
    public string? LoadedFieldType { get; set; }
    
    public FieldMetadataResolver MetadataResolver { get; }
    private readonly Action? _onFieldAnnotated;

    public Dictionary<int, (string name, bool isStatic)>? FieldMap
    {
        get
        {
            try
            {
                return MetadataResolver.GetFieldMap(Method.TypeDefIndex);
            }
            catch (Exception ex)
            {
                if (ConsoleReporter.Verbose)
                {
                    ConsoleReporter.Debug($"[CONTEXT-FAIL] Failed to get field map for type index {Method.TypeDefIndex}: {ex.Message}");
                }
                return null;
            }
        }
    }

    public Dictionary<int, string>? StaticFieldMap
    {
        get
        {
            try
            {
                return MetadataResolver.GetStaticFieldMap(Method.TypeDefIndex);
            }
            catch (Exception ex)
            {
                if (ConsoleReporter.Verbose)
                {
                    ConsoleReporter.Debug($"[CONTEXT-FAIL] Failed to get static field map for type index {Method.TypeDefIndex}: {ex.Message}");
                }
                return null;
            }
        }
    }

    public FieldAnnotationContext(
        List<IrInstruction> insts,
        DefUseIndex duIndex,
        IrMethod method,
        int index,
        IrInstruction inst,
        long offset,
        long baseReg,
        Dictionary<long, IrDataResolver.ObjectAlias> objectAliases,
        FieldMetadataResolver metadataResolver,
        Action? onFieldAnnotated = null)
    {
        Insts = insts;
        DuIndex = duIndex;
        Method = method;
        Index = index;
        Inst = inst;
        Offset = offset;
        BaseReg = baseReg;
        ObjectAliases = objectAliases;
        MetadataResolver = metadataResolver;
        _onFieldAnnotated = onFieldAnnotated;
        LoadedFieldType = null;
    }

    public void IncrementFieldsAnnotated() => _onFieldAnnotated?.Invoke();

    public void UpdateAliases()
    {
        if (Inst.Opcode is IrOpcode.Call or IrOpcode.IndirectCall)
        {
            // Call might be an object allocation returning in x0 (register 0)
            if (Inst.Destination.HasValue && Inst.Destination.Value.Kind == IrOperandKind.Register)
            {
                long destReg = Inst.Destination.Value.Value;
                if (Inst.Annotation != null)
                {
                    string tName = IrDataResolver.ExtractTypeName(Inst.Annotation);
                    if (!string.IsNullOrEmpty(tName) && !tName.Contains("il2cpp_metadata_page"))
                    {
                        ObjectAliases[destReg] = new IrDataResolver.ObjectAlias { IsThis = false, Type = ResolvedType.Parse(tName), BaseOffset = 0 };
                    }
                    else
                    {
                        ObjectAliases.Remove(destReg);
                    }
                }
                else
                {
                    ObjectAliases.Remove(destReg);
                }
            }
            else
            {
                ObjectAliases.Remove(0);
            }
        }
        else if (Inst.Destination.HasValue && Inst.Destination.Value.Kind == IrOperandKind.Register)
        {
            long destReg = Inst.Destination.Value.Value;

            // 1. Assign: xN = xM where xM is an alias
            if (Inst.Opcode == IrOpcode.Assign &&
                Inst.Sources.Length > 0 &&
                Inst.Sources[0].Kind == IrOperandKind.Register &&
                ObjectAliases.TryGetValue(Inst.Sources[0].Value, out var srcBase))
            {
                ObjectAliases[destReg] = srcBase;
            }
            // 2. Add: xN = xM + C where xM is an alias
            else if (Inst.Opcode == IrOpcode.Add &&
                     Inst.Sources.Length >= 2 &&
                     Inst.Sources[0].Kind == IrOperandKind.Register &&
                     Inst.Sources[1].Kind == IrOperandKind.Immediate &&
                     ObjectAliases.TryGetValue(Inst.Sources[0].Value, out var addBase))
            {
                if (addBase.Type.IsArray && Inst.Sources[1].Value == 0x20)
                {
                    string elemTypeName = addBase.Type.BaseType;
                    long initialOffset = 0;
                    if (MetadataResolver.TypeModel != null &&
                        MetadataResolver.TypeModel.FieldLayoutsByTypeName.TryGetValue(elemTypeName, out int typeDefIdx))
                    {
                        var typeDef = MetadataResolver.TypeModel.GetTypeDef(typeDefIdx);
                        if (typeDef != null && typeDef.IsStruct)
                        {
                            initialOffset = 0x10;
                        }
                    }
                    ObjectAliases[destReg] = new IrDataResolver.ObjectAlias
                    {
                        IsThis = false,
                        Type = ResolvedType.Parse(elemTypeName),
                        BaseOffset = initialOffset
                    };
                }
                else
                {
                    ObjectAliases[destReg] = new IrDataResolver.ObjectAlias { IsThis = addBase.IsThis, Type = addBase.Type, BaseOffset = addBase.BaseOffset + Inst.Sources[1].Value };
                }
            }
            else if (Inst.Opcode == IrOpcode.Load && LoadedFieldType != null)
            {
                // 3. Load: xN = [xM + C] where [xM + C] was resolved to a struct field
                ObjectAliases[destReg] = new IrDataResolver.ObjectAlias { IsThis = false, Type = ResolvedType.Parse(LoadedFieldType), BaseOffset = 0 };
            }
            else
            {
                // Any other write to destReg clobbers it
                ObjectAliases.Remove(destReg);
            }
        }
    }
}
