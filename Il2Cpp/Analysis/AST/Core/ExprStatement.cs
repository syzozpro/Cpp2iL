using Rosetta.Analysis.IR.SSA;
using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Analysis.AST;

/// <summary>A statement in the output — either an expression or control flow.</summary>
public sealed class ExprStatement
{
    public ExprNode Expr { get; init; } = null!;
    public IrInstruction? Inst { get; init; }
    public bool IsDeclaration { get; init; }
    public bool IsReturn { get; init; }
    public SsaVariable? SsaVar { get; init; }
}
