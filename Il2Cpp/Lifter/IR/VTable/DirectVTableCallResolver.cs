using System;
using System.Collections.Generic;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Model;
using Rosetta.Metadata;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR.VTable;

internal sealed class DirectVTableCallResolver : IVTableCallResolver
{
    public bool Resolve(VTableResolutionContext context)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"DirectVTableCallResolver: TryResolveVTableCall at idx={context.Index}");
        var icall = context.Inst;
        if (icall.Sources.Length == 0)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"DirectVTableCallResolver: TryResolveVTableCall aborted (no sources) at idx={context.Index}");
            return false;
        }
        int targetReg = (int)icall.Sources[0].Value;

        // Scan backward to find: targetReg = load [baseReg + OFFSET]
        long vtableOffset = -1;
        int baseReg = -1;
        int vtableLoadIdx = -1;

        var (loadInst, loadIdx) = IrTracingUtils.FindDefinitionSatisfying(
            context.Insts,
            context.Index,
            targetReg,
            inst => inst.Opcode == IrOpcode.Load && inst.Sources.Length > 0 && inst.Sources[0].Kind == IrOperandKind.Memory,
            8);

        if (loadInst != null)
        {
            for (int k = loadIdx + 1; k < context.Index; k++)
            {
                if (context.Insts[k].Opcode is IrOpcode.Call or IrOpcode.IndirectCall or IrOpcode.IndirectBranch)
                {
                    loadInst = null;
                    break;
                }
            }
        }

        if (loadInst != null)
        {
            vtableOffset = loadInst.Sources[0].Offset;
            baseReg = (int)loadInst.Sources[0].Value;
            vtableLoadIdx = loadIdx;
        }

        if (vtableOffset < 0 || baseReg < 0) return false;

        // Verify baseReg comes from a vtable load: baseReg = load [objReg + 0] annotated "vtable"
        bool confirmedVtable = false;
        int objReg = -1;
        int actualVTableLoadIdx = -1;
        var (vtableLoad, actualVTableIdx) = IrTracingUtils.FindDefinitionSatisfying(
            context.Insts,
            vtableLoadIdx,
            baseReg,
            inst => inst.Opcode == IrOpcode.Load && inst.SemanticTag == IrSemanticTag.VTableLoad,
            6);

        if (vtableLoad != null)
        {
            confirmedVtable = true;
            actualVTableLoadIdx = actualVTableIdx;
            if (vtableLoad.Sources.Length > 0 && vtableLoad.Sources[0].Kind == IrOperandKind.Memory)
                objReg = (int)vtableLoad.Sources[0].Value;
        }

        if (!confirmedVtable) return false;

        // Try to determine the object's type by tracing the object register backward from the actual vtable load
        string? objectTypeName = TraceObjectType(context.Insts, actualVTableLoadIdx, objReg);

        // Try to resolve the vtable slot using metadata
        string? resolvedMethod = ResolveVTableSlotFromMetadata(context.TypeModel, vtableOffset, objectTypeName);
        if (resolvedMethod != null)
        {
            icall.Annotation = resolvedMethod;
            if (icall.Opcode == IrOpcode.IndirectBranch)
                icall.Opcode = IrOpcode.TailCall;
            return true;
        }

        if (ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"DirectVTableCallResolver: Failed to resolve vtable call at idx={context.Index}. vtableOffset=0x{vtableOffset:X}, objectType='{objectTypeName ?? "unknown"}'");
        }
        return false;
    }

    private static string? TraceObjectType(List<IrInstruction> insts, int fromIdx, int objReg)
    {
        if (objReg < 0) return null;

        int currentReg = objReg;
        int currentIdx = fromIdx;
        int remainingLimit = 30;

        while (currentIdx > 0 && remainingLimit > 0)
        {
            var (inst, instIdx) = IrTracingUtils.FindDefinition(insts, currentIdx, currentReg, remainingLimit);
            if (inst == null) break;

            remainingLimit -= (currentIdx - instIdx);
            currentIdx = instIdx;

            if (!string.IsNullOrEmpty(inst.ResultType))
                return inst.ResultType;

            if (inst.Opcode == IrOpcode.Call && inst.Annotation != null)
            {
                if (inst.Annotation.StartsWith("new "))
                {
                    string typeName = inst.Annotation[4..];
                    int parenIdx = typeName.IndexOf('(');
                    if (parenIdx > 0) typeName = typeName[..parenIdx];
                    return typeName;
                }
                break;
            }

            if (inst.Opcode == IrOpcode.Assign && inst.Sources.Length > 0 &&
                inst.Sources[0].Kind == IrOperandKind.Register)
            {
                currentReg = (int)inst.Sources[0].Value;
                continue;
            }

            break;
        }

        return null;
    }

    private string? ResolveVTableSlotFromMetadata(TypeModel typeModel, long vtableOffset, string? objectTypeName)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"DirectVTableCallResolver: ResolveVTableSlotFromMetadata called with offset=0x{vtableOffset:X}, type={objectTypeName ?? "null"}");

        const int ENTRY_SIZE = 16;

        if (typeModel.VTableBaseOffset >= 0)
        {
            long relativeOffset = vtableOffset - typeModel.VTableBaseOffset;
            if (relativeOffset < 0 || relativeOffset % ENTRY_SIZE != 0) return null;
            int slotIndex = (int)(relativeOffset / ENTRY_SIZE);
            return FindMethodAtSlot(typeModel, slotIndex, objectTypeName);
        }

        if (objectTypeName == null) return null;

        lock (typeModel)
        {
            if (typeModel.VTableBaseOffset >= 0)
            {
                long relativeOffset = vtableOffset - typeModel.VTableBaseOffset;
                if (relativeOffset >= 0 && relativeOffset % ENTRY_SIZE == 0)
                {
                    int slotIndex = (int)(relativeOffset / ENTRY_SIZE);
                    return FindMethodAtSlot(typeModel, slotIndex, objectTypeName);
                }
                return null;
            }

            long validCandidateBase = -1;
            int validSlotIndex = -1;
            int validCount = 0;

            for (long candidateBase = 0x100; candidateBase <= 0x180; candidateBase += 8)
            {
                long relativeOffset = vtableOffset - candidateBase;
                if (relativeOffset < 0 || relativeOffset % ENTRY_SIZE != 0) continue;
                int slotIndex = (int)(relativeOffset / ENTRY_SIZE);

                VTableLayout? layout = typeModel.GetVTableLayout(objectTypeName);
                VTableLayout.VTableEntry? entry = layout?.GetEntryAtSlot(slotIndex);

                if (entry != null)
                {
                    MethodDefinition? methodDef = typeModel.GetMethod(entry.MethodIndex);
                    if (methodDef != null && methodDef.Name != null)
                    {
                        if (methodDef.Name.Contains('.'))
                        {
                            continue;
                        }

                        validCandidateBase = candidateBase;
                        validSlotIndex = slotIndex;
                        validCount++;
                    }
                }
            }

            if (validCount == 1)
            {
                typeModel.VTableBaseOffset = validCandidateBase;
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → Auto-detected VTable Base Offset: 0x{validCandidateBase:X} deterministically using known type {objectTypeName} (Slot {validSlotIndex})");
                
                return FindMethodAtSlot(typeModel, validSlotIndex, objectTypeName);
            }
            else if (validCount > 1)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"      → VTable Base Offset ambiguous for type {objectTypeName} at offset 0x{vtableOffset:X} ({validCount} candidates). Skipping.");
            }
        }

        return null;
    }

    private string? FindMethodAtSlot(TypeModel typeModel, int slotIndex, string? objectTypeName)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"DirectVTableCallResolver: FindMethodAtSlot called for slot={slotIndex}, type={objectTypeName ?? "null"}");
        if (slotIndex < 0 || slotIndex > 500) return null;

        if (objectTypeName != null)
        {
            var entry = typeModel.ResolveVTableMethod(objectTypeName, slotIndex);
            if (entry != null)
            {
                return $"[M:{entry.MethodIndex}] {entry.MethodName}";
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"VTable: Unresolved slot {slotIndex} for type={objectTypeName ?? "null"}");
        return null;
    }
}