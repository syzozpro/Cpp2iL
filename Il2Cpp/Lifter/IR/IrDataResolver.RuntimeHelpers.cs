using System;
using System.Collections.Generic;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Binary;
using Rosetta.Common;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Metadata;
using Rosetta.Model;
using Rosetta.Pipeline;
using Rosetta.Lifter.IR.RuntimeHelpers;

namespace Rosetta.Lifter.IR;

// ══════════════════════════════════════════════════════════════════════════
// Pass 2: Runtime Helper Classification (Refactored)
// ══════════════════════════════════════════════════════════════════════════

public sealed partial class IrDataResolver
{
    private static readonly IRuntimeHelperClassifier[] Classifiers = new IRuntimeHelperClassifier[]
    {
        new NullX0Classifier(),
        new GcWriteBarrierClassifier(),
        new BoxingClassifier(),
        new ObjectAllocationClassifier(),
        new ArrayCreationClassifier(),
        new MultiDimArrayClassifier(),
        new StringInternClassifier(),
        new GenericMethodDispatchClassifier(),
        new TypeCastClassifier(),
        new StaticFieldWriteBarrierClassifier(),
        new ChainedCallClassifier(),
        new AsyncStateMachineClassifier(),
        new ClassInitClassifier(),
        new StructFieldRefClassifier(),
        new MiscLoadFallbackClassifier(),
        new CatchAllClassifier()
    };

    private void ClassifyRuntimeHelpers(List<IrInstruction> insts, DefUseIndex duIndex)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    IrDataResolver.ClassifyRuntimeHelpers");
        for (int i = 0; i < insts.Count; i++)
        {
            var inst = insts[i];
            // Check Call, TailCall, and Branch (goto) — targets include il2cpp_runtime_helper and unresolved sub_ stubs
            if (inst.Opcode != IrOpcode.Call && inst.Opcode != IrOpcode.TailCall && inst.Opcode != IrOpcode.Branch) continue;
            if (inst.Annotation == null ||
                (!inst.Annotation.Contains("il2cpp_runtime_helper") && !inst.Annotation.StartsWith("sub_"))) continue;

            // Backward scan: find most recent x0 and x1 definitions before this call
            IrInstruction? x0Def = null;
            IrInstruction? x1Def = null;
            int x0DefIdx = -1;

            for (int j = i - 1; j >= Math.Max(0, i - 32); j--)
            {
                var prev = insts[j];
                if (prev.Destination.HasValue && prev.Destination.Value.Kind == IrOperandKind.Register)
                {
                    long regNum = prev.Destination.Value.Value;
                    if (regNum == 0 && x0Def == null) { x0Def = prev; x0DefIdx = j; }
                    if (regNum == 1 && x1Def == null) x1Def = prev;
                }
                if (x0Def != null && x1Def != null) break;
            }

            var effectiveX0Def = x0Def;
            int effectiveX0DefIdx = x0DefIdx;

            // Unwrap one level of register assignment (e.g. x0 = x8) to reveal the actual Load
            if (effectiveX0Def != null && effectiveX0Def.Opcode == IrOpcode.Assign &&
                effectiveX0Def.Sources.Length > 0 &&
                effectiveX0Def.Sources[0].Kind == IrOperandKind.Register)
            {
                long srcReg = effectiveX0Def.Sources[0].Value;
                for (int k = effectiveX0DefIdx - 1; k >= Math.Max(0, effectiveX0DefIdx - 32); k--)
                {
                    var prev = insts[k];
                    if (prev.Destination.HasValue &&
                        prev.Destination.Value.Kind == IrOperandKind.Register &&
                        prev.Destination.Value.Value == srcReg)
                    {
                        effectiveX0Def = prev;
                        effectiveX0DefIdx = k;
                        break;
                    }
                }
            }

            var context = new RuntimeHelperContext(
                insts: insts,
                duIndex: duIndex,
                index: i,
                inst: inst,
                x0Def: x0Def,
                x1Def: x1Def,
                x0DefIdx: x0DefIdx,
                effectiveX0Def: effectiveX0Def,
                effectiveX0DefIdx: effectiveX0DefIdx,
                addressMap: _addressMap,
                typeModel: _typeModel,
                metadata: _metadata,
                typeResolver: _typeResolver
            );

            string? classification = null;
            foreach (var classifier in Classifiers)
            {
                classification = classifier.Classify(context);
                if (classification != null)
                {
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Debug($"    → Classifier {classifier.GetType().Name} matched: '{classification}' at instruction idx={i} (Addr=0x{inst.Address:X})");
                    }
                    break;
                }
            }

            if (classification != null)
            {
                inst.Annotation = classification;
                RuntimeHelpersClassified++;
            }
        }
    }
}
