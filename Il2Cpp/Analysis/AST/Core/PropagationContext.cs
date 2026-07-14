using System.Collections.Generic;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Holds shared mutable state during AST expression propagation, decoupling the various
/// builder and transform modules.
/// </summary>
public sealed class PropagationContext
{
    /// <summary>The SSA context.</summary>
    public SsaContext? Ssa { get; set; }

    /// <summary>Type model for metadata-driven parameter type resolution.</summary>
    public Rosetta.Model.TypeModel? TypeModel { get; set; }

    /// <summary>Maps SSA variable → its expression tree.</summary>
    public Dictionary<SsaVariable, ExprNode> ExprMap { get; } = new();

    /// <summary>Statements per block (blockId → list of statements to emit).</summary>
    public Dictionary<int, List<ExprStatement>> BlockStatements { get; } = new();

    /// <summary>SSA variables that were inlined (don't emit as separate statements).</summary>
    public HashSet<SsaVariable> Inlined { get; } = new();

    /// <summary>Strongly-typed hints for stack slots (discovered from out parameters or subsequent assignments).</summary>
    public Dictionary<long, string> SpSlotTypes { get; } = new();

    /// <summary>Struct-return types for stack-resident values, used to resolve subfield loads.</summary>
    public Dictionary<long, string> StackStructReturnTypes { get; } = new();

    /// <summary>Registers (VarIds) with multiple SSA versions that need version suffixes to avoid clobbering.</summary>
    public HashSet<int> MultiVersionRegs { get; } = new();

    /// <summary>Tracks the last expression stored to each stack slot offset.</summary>
    public Dictionary<long, ExprNode> StackSlotValues { get; } = new();

    /// <summary>Tracks out/ref/in variables that must be declared at the top of the method.</summary>
    public Dictionary<string, VarDeclInfo> OutVariableDeclarations { get; } = new();

    /// <summary>Explicit names for stack slots (discovered from out parameters metadata).</summary>
    public string[] SpSlotNames { get; } = new string[1024];

    /// <summary>Metadata for a variable declaration injected at method scope.</summary>
    /// <param name="TypeName">The parameter's type name from metadata.</param>
    /// <param name="Kind">Whether the parameter is out, in, ref, or unknown.</param>
    /// <param name="InitialValue">Optional initial value recovered from stack stores.</param>
    public readonly record struct VarDeclInfo(string TypeName, VarDeclKind Kind, ExprNode? InitialValue = null);

    public enum VarDeclKind { Out, Ref, In, Unknown }

    /// <summary>Frame pointer SP offset: set when x29 = add SP, const is detected. -1 = not set.</summary>
    public long FpSpOffset { get; set; } = -1;

    /// <summary>Current block being processed (for peephole optimizations like box).</summary>
    public int CurrentBlockId { get; set; } = -1;

    /// <summary>Tracks the last call result expression for HFA return propagation.</summary>
    public ExprNode? LastCallResultExpr { get; set; }

    /// <summary>Tracks the HFA size of the last call result (0 if scalar, >= 2 if struct HFA).</summary>
    public int LastCallReturnHfaSize { get; set; }

    /// <summary>Instance field names of the last HFA call's return type (from metadata).</summary>
    public string[]? LastCallReturnHfaFieldNames { get; set; }

    /// <summary>Tracks which phi destinations have had their first declaration emitted.</summary>
    public HashSet<int> EmittedPhiDeclarations { get; } = new();

    /// <summary>Pre-built set of all SSA variables that appear as phi sources (for O(1) lookup).</summary>
    public HashSet<(int VarId, int Version)> PhiSourceVars { get; set; } = new();

    /// <summary>Field labels whose memcpy stores have been recovered as array literals.</summary>
    public HashSet<string> SuppressedFieldLabels { get; } = new();

    /// <summary>Stack slot offsets used exclusively for callee-saved register prologue saving.</summary>
    public HashSet<long> CalleeSavedSpillSlots { get; } = new();

    /// <summary>Pre-built SSA variable definition sites with block and address info.</summary>
    public Dictionary<int, List<(SsaVariable Var, int BlockId, ulong Addr)>> VarDefsMap { get; } = new();

    /// <summary>Pre-built mapping of SSA variable IDs and versions to their bit-widths.</summary>
    public Dictionary<(int VarId, int Version), byte> VarBitWidths { get; } = new();

    /// <summary>Pre-built mapping of memory operand base registers and offsets to their annotations.</summary>
    public Dictionary<(long BaseReg, long Offset), string> MemAnnotations { get; } = new();

    /// <summary>Namespaces referenced during expression propagation (populated from fully qualified names).</summary>
    public ICollection<string>? Usings { get; set; }
}
