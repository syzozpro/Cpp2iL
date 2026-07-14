using System;
using System.Collections.Generic;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Analysis.Resolve;
using Rosetta.Binary;
using Rosetta.Common;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Metadata;
using Rosetta.Model;
using Rosetta.Pipeline;
using Rosetta.Analysis.Utils;
using static Rosetta.Analysis.Utils.TypeAliasUtils;

namespace Rosetta.Lifter.IR;

// ══════════════════════════════════════════════════════════════════════════
// Pass 4: Field Offset Annotation + Field Map Utilities
// ══════════════════════════════════════════════════════════════════════════

public sealed partial class IrDataResolver
{
    internal struct ObjectAlias
    {
        public bool IsThis;
        public ResolvedType Type;
        public long BaseOffset;
    }

    internal void IncrementFieldsAnnotated() => FieldsAnnotated++;

    private static readonly Rosetta.Lifter.IR.FieldAnnotation.IFieldOffsetAnnotator[] Annotators = new Rosetta.Lifter.IR.FieldAnnotation.IFieldOffsetAnnotator[]
    {
        new Rosetta.Lifter.IR.FieldAnnotation.StaticFieldsPointerAnnotator(),
        new Rosetta.Lifter.IR.FieldAnnotation.StaticFieldAnnotator(),
        new Rosetta.Lifter.IR.FieldAnnotation.VTableLoadAnnotator(),
        new Rosetta.Lifter.IR.FieldAnnotation.InstanceFieldAnnotator(),
        new Rosetta.Lifter.IR.FieldAnnotation.HeuristicLengthAnnotator()
    };

    private void AnnotateFieldOffsets(List<IrInstruction> insts, DefUseIndex duIndex, IrMethod method)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"IrDataResolver: AnnotateFieldOffsets started for method={method.MethodName}");

        var cfg = new Rosetta.Analysis.IR.IrCfgBuilder().Build(method);
        if (cfg == null || cfg.Blocks.Count == 0)
        {
            RunLinearFieldAnnotation(insts, duIndex, method);
            return;
        }

        // Map instructions to their flat list index
        var instIndices = new Dictionary<IrInstruction, int>(insts.Count);
        for (int i = 0; i < insts.Count; i++)
        {
            instIndices[insts[i]] = i;
        }

        // Keep a copy of original annotations to reset them on each pass
        var originalAnnotations = new string?[insts.Count];
        for (int i = 0; i < insts.Count; i++)
        {
            originalAnnotations[i] = insts[i].Annotation;
        }

        var blockEntryStates = new Dictionary<int, Dictionary<long, ObjectAlias>>();
        var blockExitStates = new Dictionary<int, Dictionary<long, ObjectAlias>>();

        // ── Initialize object alias map for the entry block ────────────
        var entryBlock = cfg.Blocks[0];
        var entryAliases = new Dictionary<long, ObjectAlias>();
        if (!method.IsStatic)
        {
            long initialOffset = 0;
            if (method.TypeDefIndex >= 0 && _metadata != null && method.TypeDefIndex < _metadata.TypeDefinitions.Length)
            {
                var typeDef = _metadata.TypeDefinitions[method.TypeDefIndex];
                if (typeDef.IsValueType)
                {
                    initialOffset = 0x10;
                }
            }
            string thisType = method.DeclaringType ?? "Object";
            entryAliases[0] = new ObjectAlias { IsThis = true, Type = ResolvedType.Parse(thisType), BaseOffset = initialOffset };
        }

        foreach (var kvp in method.GpParamTypeMap)
        {
            int regIndex = kvp.Key;
            string paramType = kvp.Value;
            long paramInitialOffset = 0;

            if (_typeModel != null && _metadata != null)
            {
                string lookupType = paramType;
                int bracketIdx = lookupType.IndexOf('<');
                if (bracketIdx > 0) lookupType = lookupType[..bracketIdx];

                if (_typeModel.FieldLayoutsByTypeName.TryGetValue(lookupType, out int typeDefIdx))
                {
                    if (typeDefIdx >= 0 && typeDefIdx < _metadata.TypeDefinitions.Length)
                    {
                        if (_metadata.TypeDefinitions[typeDefIdx].IsValueType)
                        {
                            paramInitialOffset = 0x10;
                        }
                    }
                }
            }

            entryAliases[regIndex] = new ObjectAlias { IsThis = false, Type = ResolvedType.Parse(paramType), BaseOffset = paramInitialOffset };
        }

        blockEntryStates[entryBlock.Id] = entryAliases;

        bool changed = true;
        int maxPasses = 20;
        int pass = 0;

        while (changed && pass < maxPasses)
        {
            changed = false;
            pass++;

            // Reset annotations to original state before propagating new types
            for (int i = 0; i < insts.Count; i++)
            {
                insts[i].Annotation = originalAnnotations[i];
            }

            foreach (var block in cfg.Blocks)
            {
                if (!blockEntryStates.TryGetValue(block.Id, out var entryState))
                    continue;

                var state = new Dictionary<long, ObjectAlias>(entryState);

                for (int i = 0; i < block.Instructions.Count; i++)
                {
                    var inst = block.Instructions[i];
                    int flatIdx = instIndices[inst];
                    if (inst.Sources.Length == 0)
                    {
                        var emptyContext = new Rosetta.Lifter.IR.FieldAnnotation.FieldAnnotationContext(
                            insts, duIndex, method, flatIdx, inst, -1, -1, state, _fieldMetadataResolver, IncrementFieldsAnnotated);
                        emptyContext.UpdateAliases();
                        continue;
                    }

                    long offset = -1;
                    long baseReg = -1;

                    if (inst.Opcode == IrOpcode.Load && inst.Sources[0].Kind == IrOperandKind.Memory)
                    {
                        offset = inst.Sources[0].Offset;
                        baseReg = inst.Sources[0].Value;
                    }
                    else if (inst.Opcode == IrOpcode.Store && inst.Sources[0].Kind == IrOperandKind.Memory)
                    {
                        offset = inst.Sources[0].Offset;
                        baseReg = inst.Sources[0].Value;
                    }
                    else if (inst.Opcode == IrOpcode.Add && inst.Sources.Length >= 2 &&
                             inst.Sources[0].Kind == IrOperandKind.Register &&
                             inst.Sources[1].Kind == IrOperandKind.Immediate)
                    {
                        baseReg = inst.Sources[0].Value;
                        offset = inst.Sources[1].Value;
                    }

                    var context = new Rosetta.Lifter.IR.FieldAnnotation.FieldAnnotationContext(
                        insts, duIndex, method, flatIdx, inst, offset, baseReg, state, _fieldMetadataResolver, IncrementFieldsAnnotated);

                    if (baseReg != -1 && (inst.Annotation == null || inst.Annotation.StartsWith("sign-extend")))
                    {
                        if (baseReg != 31)
                        {
                            foreach (var annotator in Annotators)
                            {
                                if (annotator.Annotate(context))
                                {
                                    break;
                                }
                            }
                        }
                    }

                    context.UpdateAliases();
                }

                bool exitStateChanged = false;
                if (!blockExitStates.TryGetValue(block.Id, out var prevExit))
                {
                    blockExitStates[block.Id] = state;
                    exitStateChanged = true;
                }
                else
                {
                    if (!AreAliasMapsEqual(state, prevExit))
                    {
                        blockExitStates[block.Id] = state;
                        exitStateChanged = true;
                    }
                }

                if (exitStateChanged)
                {
                    foreach (var successor in block.Successors)
                    {
                        if (!blockEntryStates.TryGetValue(successor.Target.Id, out var succEntry))
                        {
                            blockEntryStates[successor.Target.Id] = new Dictionary<long, ObjectAlias>(state);
                            changed = true;
                        }
                        else
                        {
                            if (MergeAliasMaps(succEntry, state))
                            {
                                changed = true;
                            }
                        }
                    }
                }
            }
        }
    }

    private static bool AreAliasMapsEqual(Dictionary<long, ObjectAlias> a, Dictionary<long, ObjectAlias> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var val)) return false;
            if (val.IsThis != kvp.Value.IsThis) return false;
            if (val.BaseOffset != kvp.Value.BaseOffset) return false;
            if (val.Type.OriginalName != kvp.Value.Type.OriginalName) return false;
        }
        return true;
    }

    private static bool MergeAliasMaps(Dictionary<long, ObjectAlias> dest, Dictionary<long, ObjectAlias> src)
    {
        bool changed = false;
        var keysToRemove = new List<long>();

        foreach (var kvp in dest)
        {
            if (!src.TryGetValue(kvp.Key, out var srcVal))
            {
                keysToRemove.Add(kvp.Key);
            }
            else
            {
                if (srcVal.IsThis != kvp.Value.IsThis ||
                    srcVal.BaseOffset != kvp.Value.BaseOffset ||
                    srcVal.Type.OriginalName != kvp.Value.Type.OriginalName)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
        }

        if (keysToRemove.Count > 0)
        {
            foreach (long key in keysToRemove)
            {
                dest.Remove(key);
            }
            changed = true;
        }

        return changed;
    }

    private void RunLinearFieldAnnotation(List<IrInstruction> insts, DefUseIndex duIndex, IrMethod method)
    {
        var objectAliases = new Dictionary<long, ObjectAlias>();
        if (!method.IsStatic)
        {
            long initialOffset = 0;
            if (method.TypeDefIndex >= 0 && _metadata != null && method.TypeDefIndex < _metadata.TypeDefinitions.Length)
            {
                var typeDef = _metadata.TypeDefinitions[method.TypeDefIndex];
                if (typeDef.IsValueType)
                {
                    initialOffset = 0x10;
                }
            }
            string thisType = method.DeclaringType ?? "Object";
            objectAliases[0] = new ObjectAlias { IsThis = true, Type = ResolvedType.Parse(thisType), BaseOffset = initialOffset };
        }

        foreach (var kvp in method.GpParamTypeMap)
        {
            int regIndex = kvp.Key;
            string paramType = kvp.Value;
            long paramInitialOffset = 0;

            if (_typeModel != null && _metadata != null)
            {
                string lookupType = paramType;
                int bracketIdx = lookupType.IndexOf('<');
                if (bracketIdx > 0) lookupType = lookupType[..bracketIdx];

                if (_typeModel.FieldLayoutsByTypeName.TryGetValue(lookupType, out int typeDefIdx))
                {
                    if (typeDefIdx >= 0 && typeDefIdx < _metadata.TypeDefinitions.Length)
                    {
                        if (_metadata.TypeDefinitions[typeDefIdx].IsValueType)
                        {
                            paramInitialOffset = 0x10;
                        }
                    }
                }
            }

            objectAliases[regIndex] = new ObjectAlias { IsThis = false, Type = ResolvedType.Parse(paramType), BaseOffset = paramInitialOffset };
        }

        for (int i = 0; i < insts.Count; i++)
        {
            var inst = insts[i];
            if (inst.Sources.Length == 0)
            {
                var emptyContext = new Rosetta.Lifter.IR.FieldAnnotation.FieldAnnotationContext(
                    insts, duIndex, method, i, inst, -1, -1, objectAliases, _fieldMetadataResolver, IncrementFieldsAnnotated);
                emptyContext.UpdateAliases();
                continue;
            }

            long offset = -1;
            long baseReg = -1;

            if (inst.Opcode == IrOpcode.Load && inst.Sources[0].Kind == IrOperandKind.Memory)
            {
                offset = inst.Sources[0].Offset;
                baseReg = inst.Sources[0].Value;
            }
            else if (inst.Opcode == IrOpcode.Store && inst.Sources[0].Kind == IrOperandKind.Memory)
            {
                offset = inst.Sources[0].Offset;
                baseReg = inst.Sources[0].Value;
            }
            else if (inst.Opcode == IrOpcode.Add && inst.Sources.Length >= 2 &&
                     inst.Sources[0].Kind == IrOperandKind.Register &&
                     inst.Sources[1].Kind == IrOperandKind.Immediate)
            {
                baseReg = inst.Sources[0].Value;
                offset = inst.Sources[1].Value;
            }

            var context = new Rosetta.Lifter.IR.FieldAnnotation.FieldAnnotationContext(
                insts, duIndex, method, i, inst, offset, baseReg, objectAliases, _fieldMetadataResolver, IncrementFieldsAnnotated);

            if (baseReg != -1 && (inst.Annotation == null || inst.Annotation.StartsWith("sign-extend")))
            {
                if (baseReg != 31)
                {
                    foreach (var annotator in Annotators)
                    {
                        if (annotator.Annotate(context))
                        {
                            break;
                        }
                    }
                }
            }

            context.UpdateAliases();
        }
    }
}
