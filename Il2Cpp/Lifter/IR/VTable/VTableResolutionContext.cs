using System;
using System.Collections.Generic;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Model;
using Rosetta.Metadata;

namespace Rosetta.Lifter.IR.VTable;

internal sealed class VTableResolutionContext
{
    public List<IrInstruction> Insts { get; }
    public int Index { get; }
    public IrInstruction Inst { get; }
    public TypeModel TypeModel { get; }
    public MetadataParser Metadata { get; }
    public Func<string, int> FindTypeDefIndex { get; }

    public VTableResolutionContext(List<IrInstruction> insts, int index, IrInstruction inst, TypeModel typeModel, MetadataParser metadata, Func<string, int> findTypeDefIndex)
    {
        Insts = insts;
        Index = index;
        Inst = inst;
        TypeModel = typeModel;
        Metadata = metadata;
        FindTypeDefIndex = findTypeDefIndex;
    }
}
