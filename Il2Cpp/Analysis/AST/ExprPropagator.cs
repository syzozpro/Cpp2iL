using System.Collections.Generic;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Lifter.IR;
using Rosetta.Model;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

/// <summary>
/// SSA-based expression propagator.
///
/// Walks CFG blocks using SSA def-use chains to build expression trees.
/// Single-use definitions are inlined into their use sites automatically.
/// No backward scans. No pattern matching. Just data flow.
///
/// Pipeline: SSA → ExprPropagator → ExprMap (per SSA variable) + Statement list
///
/// This is the coordinator (partial class). Expression builders, SSA resolution,
/// type decoders, and helpers live in separate files under Builders/, Resolution/,
/// and Helpers/ subdirectories.
/// </summary>
public sealed partial class ExprPropagator
{
    private readonly IrMethod _method;
    public IrMethod Method => _method;
    private readonly IrControlFlowGraph _cfg;
    private readonly SsaContext _ssa;
    private readonly DefUseAnalyzer _defUse;
    private readonly string? _declaringType;

    /// <summary>Type model for metadata-driven parameter type resolution at call sites.</summary>
    private readonly TypeModel? _typeModel;

    private readonly PropagationContext _ctx = new();
    public PropagationContext Ctx => _ctx;

    private readonly HashSet<long> _declaredSpSlots = new();

    // ═══ Output ═══
    /// <summary>Maps SSA variable → its expression tree.</summary>
    public Dictionary<SsaVariable, ExprNode> ExprMap => _ctx.ExprMap;

    /// <summary>Statements per block (blockId → list of statements to emit).</summary>
    public Dictionary<int, List<ExprStatement>> BlockStatements => _ctx.BlockStatements;

    /// <summary>SSA variables that were inlined (don't emit as separate statements).</summary>
    public HashSet<SsaVariable> Inlined => _ctx.Inlined;

    /// <summary>Resolver for FieldRVA data (array literal initializers).</summary>
    private readonly Resolve.FieldRvaResolver? _fieldRvaResolver;

    public ExprPropagator(IrMethod method, IrControlFlowGraph cfg, SsaContext ssa,
        Resolve.FieldRvaResolver? fieldRvaResolver = null, TypeModel? typeModel = null, ICollection<string>? usings = null)
    {
        _method = method;
        _cfg = cfg;
        _ssa = ssa;
        _defUse = new DefUseAnalyzer(ssa);
        _declaringType = method.DeclaringType;
        _fieldRvaResolver = fieldRvaResolver;
        _typeModel = typeModel;
        _ctx.TypeModel = typeModel;
        _ctx.Ssa = ssa;
        _ctx.Usings = usings;

        IndexVariableDefinitions(ssa, cfg);
        IndexMemoryAnnotations(cfg);

        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"ExprPropagator() created for {method.DeclaringType}::{method.MethodName} (static={method.IsStatic}, params={method.Parameters.Count})");
    }

    /// <summary>Run the propagation pass.</summary>
    public void Propagate()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"Propagate() START for {_method.DeclaringType}::{_method.MethodName}");

        InitializeMultiVersionRegisters();
        InitializePhiSourceSet();
        var rpoOrder = GetReachableBlockOrder();
        _declaredSpSlots.Clear(); // Tracks which stack slots have been declared in this method

        ProcessReachableBlocks(rpoOrder);
        DeconstructPhis(rpoOrder);
        RemoveDeadStackStores();
        LogPropagationSummary();
    }
}
