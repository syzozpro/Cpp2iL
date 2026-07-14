using System;
using System.Collections.Generic;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR;

// ══════════════════════════════════════════════════════════════════════════
// Pass 6: VTable Indirect Call Resolution
// ══════════════════════════════════════════════════════════════════════════

public sealed partial class IrDataResolver
{
    private static readonly Rosetta.Lifter.IR.VTable.IVTableCallResolver[] VTableCallResolvers = new Rosetta.Lifter.IR.VTable.IVTableCallResolver[]
    {
        new Rosetta.Lifter.IR.VTable.DirectVTableCallResolver(),
        new Rosetta.Lifter.IR.VTable.InterfaceCallResolver()
    };

    private void ResolveVTableCalls(List<IrInstruction> insts)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace("IrDataResolver: ResolveVTableCalls started");
        if (_typeModel == null || _metadata == null)
        {
            if (ConsoleReporter.IsTracing) ConsoleReporter.Debug("IrDataResolver: ResolveVTableCalls aborted (missing _typeModel or _metadata)");
            return;
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"IrDataResolver: Processing {insts.Count} instructions for VTable calls");
        for (int i = 0; i < insts.Count; i++)
        {
            if (insts[i].Opcode != IrOpcode.IndirectCall && insts[i].Opcode != IrOpcode.IndirectBranch) continue;
            if (insts[i].Annotation != null) continue; // already resolved

            var context = new Rosetta.Lifter.IR.VTable.VTableResolutionContext(insts, i, insts[i], _typeModel, _metadata, FindTypeDefIndex);
            foreach (var resolver in VTableCallResolvers)
            {
                if (resolver.Resolve(context))
                {
                    break;
                }
            }

            // Post-resolution target return register adjustment
            string? ann = insts[i].Annotation;
            if (ann != null && ann.StartsWith("[M:"))
            {
                int colonIdx = ann.IndexOf(':');
                int bracketIdx = ann.IndexOf(']');
                if (colonIdx > 0 && bracketIdx > colonIdx)
                {
                    string mIdxStr = ann.Substring(colonIdx + 1, bracketIdx - colonIdx - 1);
                    if (int.TryParse(mIdxStr, out int methodIdx))
                    {
                        var methodDef = _typeModel.GetMethod(methodIdx);
                        if (methodDef != null)
                        {
                            string retType = _typeResolver != null 
                                ? _typeResolver.ResolveTypeName(methodDef.ReturnTypeIndex) 
                                : "object";
                            retType = Rosetta.Common.TypeUtils.CleanTypeName(retType);

                            int hfaSize = _typeResolver != null 
                                ? _typeResolver.GetHfaSize(methodDef.ReturnTypeIndex) 
                                : 0;

                            if (hfaSize >= 2)
                            {
                                insts[i].Destination = null;
                            }
                            else if (retType is "float" or "System.Single" or "Single")
                            {
                                insts[i].Destination = IrOperand.FpRegister(0, 32);
                            }
                            else if (retType is "double" or "System.Double" or "Double")
                            {
                                insts[i].Destination = IrOperand.FpRegister(0, 64);
                            }
                            else if (retType == "void" || retType == "System.Void")
                            {
                                insts[i].Destination = null;
                            }
                        }
                    }
                }
            }
        }
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace("IrDataResolver: ResolveVTableCalls finished");
    }
}
