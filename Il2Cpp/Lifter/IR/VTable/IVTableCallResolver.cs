namespace Rosetta.Lifter.IR.VTable;

internal interface IVTableCallResolver
{
    bool Resolve(VTableResolutionContext context);
}
