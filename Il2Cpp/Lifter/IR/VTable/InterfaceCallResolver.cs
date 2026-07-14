using System;
using System.Collections.Generic;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Model;
using Rosetta.Metadata;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR.VTable;

internal sealed class InterfaceCallResolver : IVTableCallResolver
{
    public bool Resolve(VTableResolutionContext context)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"InterfaceCallResolver: TryResolveInterfaceCall at idx={context.Index}");
        
        var icall = context.Inst;
        if (icall.Sources.Length == 0) return false;
        int targetReg = (int)icall.Sources[0].Value;

        // Step 1: Find the load that defines the indirect call target register
        // Pattern: targetReg = load [computedBase + methodOffset]
        long methodOffset = 0;
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
            methodOffset = loadInst.Sources[0].Offset;
        }

        // Step 2: Scan backward for a Compare with typeof() to identify the interface
        string? interfaceTypeName = null;

        for (int j = context.Index - 1; j >= Math.Max(0, context.Index - 100); j--)
        {
            var inst = context.Insts[j];
            if (inst.Opcode != IrOpcode.Compare) continue;
            if (inst.Sources.Length < 2) continue;

            for (int operand = 0; operand < 2; operand++)
            {
                var src = inst.Sources[operand];
                if (src.Kind != IrOperandKind.Register) continue;

                string? traced = TraceToTypeOf(context.Insts, j, (int)src.Value);
                if (traced != null)
                {
                    interfaceTypeName = traced;
                    goto found;
                }
            }
        }

        found:
        if (interfaceTypeName == null)
        {
            if (ConsoleReporter.Verbose)
            {
                ConsoleReporter.Debug($"InterfaceCallResolver: Failed to find interface name for indirect call at idx={context.Index}");
            }
            return false;
        }
        
        // Step 3: Compute method slot from the vtable load offset
        const int ENTRY_SIZE = 16;
        int methodSlot = (methodOffset >= 0 && methodOffset % ENTRY_SIZE == 0)
            ? (int)(methodOffset / ENTRY_SIZE)
            : 0;

        // Step 4: Resolve using the actual slot index
        string? resolved = ResolveInterfaceMethod(context.Metadata, context.FindTypeDefIndex, interfaceTypeName, methodSlot);
        if (resolved != null)
        {
            icall.Annotation = resolved;
            if (icall.Opcode == IrOpcode.IndirectBranch)
                icall.Opcode = IrOpcode.TailCall;
            return true;
        }

        if (ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"InterfaceCallResolver: Failed to resolve interface method for {interfaceTypeName} at slot {methodSlot} (offset 0x{methodOffset:X}) at idx={context.Index}");
        }
        return false;
    }

    private static string? TraceToTypeOf(List<IrInstruction> insts, int fromIdx, int reg)
    {
        int currentReg = reg;
        int currentIdx = fromIdx;

        for (int depth = 0; depth < 3; depth++)
        {
            var (prev, prevIdx) = IrTracingUtils.FindDefinition(insts, currentIdx, currentReg, 100);
            if (prev == null) return null;

            if (prev.Annotation != null &&
                prev.Annotation.StartsWith("typeof(") &&
                prev.Annotation.EndsWith(")"))
            {
                return prev.Annotation[7..^1];
            }

            if (prev.Opcode == IrOpcode.Load &&
                prev.Sources.Length > 0 &&
                prev.Sources[0].Kind == IrOperandKind.Memory)
            {
                currentReg = (int)prev.Sources[0].Value;
                currentIdx = prevIdx;
                continue;
            }

            return null;
        }

        return null;
    }

    private string? ResolveInterfaceMethod(MetadataParser metadata, Func<string, int> findTypeDefIndex, string interfaceTypeName, int methodOffset)
    {
        string metadataName = ToMetadataTypeName(interfaceTypeName);
        int typeDefIdx = findTypeDefIndex(metadataName);

        if (typeDefIdx < 0) typeDefIdx = findTypeDefIndex(interfaceTypeName);
        if (typeDefIdx < 0) return null;

        var typeDef = metadata.TypeDefinitions[typeDefIdx];
        if (typeDef.MethodCount <= methodOffset) return null;

        int methodIdx = typeDef.MethodStart + methodOffset;
        if (methodIdx >= metadata.MethodDefinitions.Length) return null;

        var method = metadata.MethodDefinitions[methodIdx];
        if (method.Name == null) return null;

        return $"[M:{methodIdx}] {interfaceTypeName}::{method.Name}";
    }

    private static string ToMetadataTypeName(string annotatedName)
    {
        int angleBracket = annotatedName.IndexOf('<');
        if (angleBracket < 0) return annotatedName;

        string baseName = annotatedName[..angleBracket];

        int depth = 0;
        int commaCount = 0;
        for (int i = angleBracket + 1; i < annotatedName.Length - 1; i++)
        {
            if (annotatedName[i] == '<') depth++;
            else if (annotatedName[i] == '>') depth--;
            else if (annotatedName[i] == ',' && depth == 0) commaCount++;
        }

        return $"{baseName}`{commaCount + 1}";
    }
}
