using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Analysis.IR;
using Rosetta.Analysis.IR.SSA;
using Rosetta.Common;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Pipeline;
using Rosetta.Analysis.Utils;
using Rosetta.Config;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Analysis.AST.Utils;

namespace Rosetta.Analysis.AST;

/// <summary>
/// SSA-based AST builder. Replaces the pattern-matching AstBuilder.
///
/// Uses ExprPropagator to build expression trees from SSA def-use chains,
/// then structures control flow by walking the CFG in RPO order.
///
/// No backward scans. No canonical name tracking. No special cases.
/// </summary>
public sealed class SsaAstBuilder
{
    // RPO block order for correct emission ordering
    private List<int> _rpoOrder = new();
    // RPO index lookup: blockId → position in _rpoOrder (O(1) vs List.IndexOf O(n))
    private Dictionary<int, int> _rpoIndex = new();
    private AstBlock? _rootBlock;
    // Natural loop metadata: loop header block ID → loop info
    private Dictionary<int, LoopInfo> _loops = new();

    private AstTranslator _translator = null!;
    private Rosetta.Lifter.IR.IrMethod _method = null!;

    /// <summary>Build an AstMethod from analysis results.</summary>
    public AstMethod? Build(MethodAnalysisResult result, ICollection<string>? usings = null, object? typeModel = null,
        Resolve.FieldRvaResolver? fieldRvaResolver = null)
    {
        if (result.Cfg == null || result.Ssa == null || result.IrMethod == null)
            return null;

        var method = result.IrMethod;
        _method = method;
        var cfg = result.Cfg;
        var ssa = result.Ssa;

        // Phase 1: Run expression propagation
        var propagator = new ExprPropagator(method, cfg, ssa, fieldRvaResolver,
            typeModel as Model.TypeModel, usings);
        propagator.Propagate();

        // Phase 2: Structure control flow + emit statements
        _rpoOrder = cfg.ReachableReversePostOrder().Select(b => b.Id).ToList();
        _rpoIndex = new Dictionary<int, int>(_rpoOrder.Count);
        for (int i = 0; i < _rpoOrder.Count; i++)
            _rpoIndex[_rpoOrder[i]] = i;

        // Phase 2a: Detect natural loops from CFG back-edges
        if (!Il2cppConfig.DisableLoopReconstructure)
            _loops = LoopDetector.DetectNaturalLoops(cfg);

        var body = new AstBlock();
        _rootBlock = body;
        _translator = new AstTranslator(body);
        
        var visited = new HashSet<int>();

        foreach (int blockId in _rpoOrder)
        {
            if (!visited.Add(blockId)) continue;
            EmitBlock(blockId, cfg, ssa, propagator, body, visited);
        }

        // Phase 3: Inject top-level declarations for out/ref/in variables
        foreach (var kvp in propagator.Ctx.OutVariableDeclarations)
        {
            string cleanType = Rosetta.Common.TypeUtils.StripModifiers(kvp.Value.TypeName);
            cleanType = Rosetta.Analysis.Utils.StringUtils.CleanTypeNameAndAddUsing(cleanType, propagator.Ctx.Usings);

            AstExpression? initExpr = null;
            if (kvp.Value.Kind != PropagationContext.VarDeclKind.Out)
            {
                initExpr = kvp.Value.InitialValue != null
                    ? AstTranslator.TranslateExpr(kvp.Value.InitialValue, propagator.Ctx.SpSlotNames, ssa)
                    : new AstIdentifier { Name = "default" };
            }

            var existingDecl = body.Statements.OfType<AstVariableDeclaration>().FirstOrDefault(d => d.VarName == kvp.Key);
            if (existingDecl != null)
            {
                existingDecl.TypeName = cleanType;
                existingDecl.IsTypeResolved = true;
                if (initExpr != null)
                    existingDecl.Initializer = initExpr;
                continue;
            }

            body.Statements.Insert(0, new AstVariableDeclaration
            {
                TypeName = cleanType,
                VarName = kvp.Key,
                Initializer = initExpr
            });
        }

        var astMethod = new AstMethod
        {
            MethodName = method.MethodName ?? "?",
            DeclaringType = method.DeclaringType,
            ReturnType = method.ReturnType ?? "void",
            Parameters = method.Parameters?.ToList() ?? new(),
            IsStatic = method.IsStatic,
            OutVariableDeclarations = propagator.Ctx.OutVariableDeclarations.Keys.ToHashSet(),
            Body = body
        };

        if (!Il2cppConfig.DisableNamingStyle)
        {
            NamingStylePass.ApplyNamingStyle(astMethod);
        }

        return astMethod;
    }

    private bool IsTerminal(IrBasicBlock block)
    {
        return block.TerminatorKind == IrTerminatorKind.Return || block.TerminatorKind == IrTerminatorKind.TailCall;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CFG Structuring — walk blocks in RPO, emit if/else
    // ═══════════════════════════════════════════════════════════════════════

    private void EmitBlock(int startBlockId, IrControlFlowGraph cfg, SsaContext ssa,
        ExprPropagator propagator, AstBlock output, HashSet<int> visited, HashSet<int>? stopAt = null)
    {
        int blockId = startBlockId;
        while (true)
        {
            if (ConsoleReporter.IsTracing)
                ConsoleReporter.Trace($"EmitBlock: blockId={blockId}, stopAt=[{(stopAt != null ? string.Join(", ", stopAt) : "null")}]");

            if (stopAt != null && stopAt.Contains(blockId))
            {
                if (ConsoleReporter.IsTracing)
                    ConsoleReporter.Debug($"EmitBlock: blockId={blockId} stopped because it is in stopAt");
                visited.Remove(blockId);
                return;
            }

            var block = cfg.FindBlock(blockId);
            if (block == null) return;

            int precomputedMergeId = -1;
            if (block.TerminatorKind == IrTerminatorKind.ConditionalBranch && block.Successors.Count >= 2)
            {
                precomputedMergeId = FindMergePoint(block.Successors[0].Target.Id, block.Successors[1].Target.Id, cfg, blockId, ssa);
            }

            if (precomputedMergeId >= 0 && !IsLoopHeader(precomputedMergeId, cfg))
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"Block {blockId} merges at {precomputedMergeId}. Emitting Phis.");
                foreach (var phi in ssa.GetPhis(precomputedMergeId))
                {
                    if (phi.Destination.IsStackSlot && propagator.Ctx.CalleeSavedSpillSlots.Contains(phi.Destination.VarId - 200))
                    {
                        continue;
                    }
                    string typeName = _translator.InferPhiType(phi.Destination, propagator, cfg);
                    output.Statements.Add(new AstVariableDeclaration
                    {
                        TypeName = typeName,
                        VarName = propagator.GetVarName(phi.Destination)
                    });
                }
            }

            if (_loops.ContainsKey(blockId))
            {
                if (ConsoleReporter.IsTracing)
                    ConsoleReporter.Debug($"EmitBlock: blockId={blockId} is loop header -> routing to EmitLoop");
                EmitLoop(blockId, cfg, ssa, propagator, output, visited, stopAt);
                return;
            }

            if (ConsoleReporter.IsTracing)
                ConsoleReporter.Trace($"EmitBlock: blockId={blockId} emitting statements...");
            _translator.EmitStatements(blockId, propagator, output);

            if (block.TerminatorKind == IrTerminatorKind.ConditionalBranch &&
                block.Successors.Count >= 2)
            {
                var thenEdge = block.Successors[0];
                var elseEdge = block.Successors[1];
                AstExpression condExpr = _translator.BuildConditionExpr(block, ssa, propagator);

                if (ConsoleReporter.IsTracing)
                    ConsoleReporter.Debug($"EmitBlock {blockId}: Conditional branch (then={thenEdge.Target.Id}, else={elseEdge.Target.Id}) cond={Rosetta.CodeGen.CSharpEmitter.EmitExpr(condExpr)}");

                bool thenIsLoopHeader = stopAt != null && _loops.ContainsKey(thenEdge.Target.Id) && stopAt.Contains(thenEdge.Target.Id);
                bool elseIsLoopHeader = stopAt != null && _loops.ContainsKey(elseEdge.Target.Id) && stopAt.Contains(elseEdge.Target.Id);

                if (thenIsLoopHeader && !elseIsLoopHeader)
                {
                    bool elseExits = (stopAt != null && stopAt.Contains(elseEdge.Target.Id)) || IsTerminal(elseEdge.Target);
                    if (elseExits)
                    {
                        if (ConsoleReporter.IsTracing)
                            ConsoleReporter.Debug($"EmitBlock {blockId}: thenIsLoopHeader is true, elseExits is true. Emitting negated condition if-break, then continuing with else branch {elseEdge.Target.Id}.");
                        output.Statements.Add(new AstIfStatement
                        {
                            Condition = AstTranslator.NegateCondition(condExpr),
                            ThenBody = new AstBlock { Statements = { new AstBreakStatement() } }
                        });
                    }
                    else
                    {
                        if (ConsoleReporter.IsTracing)
                            ConsoleReporter.Debug($"EmitBlock {blockId}: thenIsLoopHeader is true, elseExits is false. Emitting condition if-continue, then continuing with else branch {elseEdge.Target.Id}.");
                        output.Statements.Add(new AstIfStatement
                        {
                            Condition = condExpr,
                            ThenBody = new AstBlock { Statements = { new AstContinueStatement() } }
                        });
                    }
                    if (visited.Add(elseEdge.Target.Id) || IsTerminal(elseEdge.Target))
                    {
                        blockId = elseEdge.Target.Id;
                        continue;
                    }
                    return;
                }
                else if (elseIsLoopHeader && !thenIsLoopHeader)
                {
                    bool thenExits = (stopAt != null && stopAt.Contains(thenEdge.Target.Id)) || IsTerminal(thenEdge.Target);
                    if (thenExits)
                    {
                        if (ConsoleReporter.IsTracing)
                            ConsoleReporter.Debug($"EmitBlock {blockId}: elseIsLoopHeader is true, thenExits is true. Emitting condition if-break, then continuing with then branch {thenEdge.Target.Id}.");
                        output.Statements.Add(new AstIfStatement
                        {
                            Condition = condExpr,
                            ThenBody = new AstBlock { Statements = { new AstBreakStatement() } }
                        });
                    }
                    else
                    {
                        if (ConsoleReporter.IsTracing)
                            ConsoleReporter.Debug($"EmitBlock {blockId}: elseIsLoopHeader is true, thenExits is false. Emitting negated condition if-continue, then continuing with then branch {thenEdge.Target.Id}.");
                        output.Statements.Add(new AstIfStatement
                        {
                            Condition = AstTranslator.NegateCondition(condExpr),
                            ThenBody = new AstBlock { Statements = { new AstContinueStatement() } }
                        });
                    }
                    if (visited.Add(thenEdge.Target.Id) || IsTerminal(thenEdge.Target))
                    {
                        blockId = thenEdge.Target.Id;
                        continue;
                    }
                    return;
                }

                int innerMerge = precomputedMergeId;
                var innerStop = stopAt != null ? new HashSet<int>(stopAt) : new HashSet<int>();
                if (innerMerge >= 0) innerStop.Add(innerMerge);

                var thenBody = new AstBlock();
                var elseBody = new AstBlock();

                if (ConsoleReporter.IsTracing)
                    ConsoleReporter.Debug($"EmitBlock {blockId}: Emitting thenBody for {thenEdge.Target.Id} and elseBody for {elseEdge.Target.Id}");

                if (visited.Add(thenEdge.Target.Id) || IsTerminal(thenEdge.Target))
                    EmitBlock(thenEdge.Target.Id, cfg, ssa, propagator, thenBody, visited, innerStop);
                if (visited.Add(elseEdge.Target.Id) || IsTerminal(elseEdge.Target))
                    EmitBlock(elseEdge.Target.Id, cfg, ssa, propagator, elseBody, visited, innerStop);

                if (thenBody.Statements.Count == 0 && elseBody.Statements.Count == 0)
                {
                    // Both branches are empty; do not emit the if statement.
                }
                else if (thenBody.Statements.Count == 0 && elseBody.Statements.Count > 0)
                {
                    output.Statements.Add(new AstIfStatement
                    {
                        Condition = AstTranslator.NegateCondition(condExpr),
                        ThenBody = elseBody,
                        ElseBody = null
                    });
                }
                else
                {
                    output.Statements.Add(new AstIfStatement
                    {
                        Condition = condExpr,
                        ThenBody = thenBody,
                        ElseBody = elseBody.Statements.Count > 0 ? elseBody : null
                    });
                }

                if (innerMerge >= 0 && visited.Add(innerMerge))
                {
                    blockId = innerMerge;
                    continue;
                }
                return;
            }
            else if (block.TerminatorKind == IrTerminatorKind.Return || block.TerminatorKind == IrTerminatorKind.TailCall)
            {
                if (ConsoleReporter.IsTracing)
                    ConsoleReporter.Debug($"EmitBlock {blockId}: Terminal Return/TailCall block");
                var lastInst = block.Instructions.LastOrDefault();
                if (lastInst?.Opcode == IrOpcode.Return || lastInst?.Opcode == IrOpcode.TailCall)
                {
                    var ret = new AstReturnStatement();
                    if (_translator.LastReturnExpr != null)
                    {
                        var retVal = _translator.LastReturnExpr;
                        string rt = _method.ReturnType ?? "";
                        if ((rt == "bool" || rt == "System.Boolean") && retVal is AstLiteral lit && lit.Value is int i)
                        {
                            retVal = new AstLiteral { Value = (i != 0) };
                        }
                        ret.Value = retVal;
                        _translator.LastReturnExpr = null;
                    }
                    output.Statements.Add(ret);
                }
                return;
            }
            else if (block.Successors.Count == 1)
            {
                var next = block.Successors[0].Target;
                if (ConsoleReporter.IsTracing)
                    ConsoleReporter.Debug($"EmitBlock {blockId}: Single successor {next.Id}");
                if (visited.Add(next.Id) || IsTerminal(next))
                {
                    blockId = next.Id;
                    continue;
                }
                return;
            }
            else
            {
                return;
            }
        }
    }

    private int FindMergePoint(int thenBlockId, int elseBlockId, IrControlFlowGraph cfg, int callerBlockId, SsaContext ssa)
    {
        if (thenBlockId == elseBlockId) return thenBlockId;

        int callerRpo = _rpoIndex.TryGetValue(callerBlockId, out var cRpo) ? cRpo : int.MaxValue;
        int thenRpo = _rpoIndex.TryGetValue(thenBlockId, out var tRpo) ? tRpo : -1;
        int elseRpo = _rpoIndex.TryGetValue(elseBlockId, out var eRpo) ? eRpo : -1;

        bool hasBackEdge = thenRpo < callerRpo || elseRpo < callerRpo;

        var thenReachable = CollectReachable(thenBlockId, cfg, forwardOnly: !hasBackEdge);
        var elseReachable = CollectReachable(elseBlockId, cfg, forwardOnly: !hasBackEdge);

        int bestId = -1;
        int bestRpo = int.MaxValue;
        foreach (int id in thenReachable)
        {
            if (elseReachable.Contains(id))
            {
                if (!ssa.DomTree.Dominates(callerBlockId, id))
                {
                    continue;
                }

                int rpoIdx = _rpoIndex.TryGetValue(id, out var idx) ? idx : -1;
                if (rpoIdx >= 0 && rpoIdx < bestRpo)
                {
                    bestRpo = rpoIdx;
                    bestId = id;
                }
            }
        }

        return bestId;
    }

    private HashSet<int> CollectReachable(int startId, IrControlFlowGraph cfg, bool forwardOnly)
    {
        var reachable = new HashSet<int>();
        var worklist = new Queue<int>();
        worklist.Enqueue(startId);

        while (worklist.Count > 0)
        {
            int id = worklist.Dequeue();
            if (!reachable.Add(id)) continue;
            var block = cfg.FindBlock(id);
            if (block == null) continue;

            if (forwardOnly)
            {
                int currentRpo = _rpoIndex.TryGetValue(id, out var crpo) ? crpo : int.MaxValue;
                foreach (var edge in block.Successors)
                {
                    int targetRpo = _rpoIndex.TryGetValue(edge.Target.Id, out var trpo) ? trpo : -1;
                    if (targetRpo > currentRpo)
                        worklist.Enqueue(edge.Target.Id);
                }
            }
            else
            {
                foreach (var edge in block.Successors)
                    worklist.Enqueue(edge.Target.Id);
            }
        }

        return reachable;
    }

    private bool IsLoopHeader(int blockId, IrControlFlowGraph cfg)
    {
        var block = cfg.FindBlock(blockId);
        if (block == null) return false;
        int blockRpo = _rpoIndex.TryGetValue(blockId, out var rpo) ? rpo : -1;
        foreach (var pred in block.Predecessors)
        {
            int predRpo = _rpoIndex.TryGetValue(pred.Source.Id, out var pr) ? pr : -1;
            if (predRpo > blockRpo)
                return true;
        }
        return false;
    }

    private void EmitLoop(int headerBlockId, IrControlFlowGraph cfg, SsaContext ssa,
        ExprPropagator propagator, AstBlock output, HashSet<int> visited, HashSet<int>? outerStopAt)
    {
        if (ConsoleReporter.IsTracing)
            ConsoleReporter.Debug($"EmitLoop: headerBlockId={headerBlockId}, exitBlockId={_loops[headerBlockId].ExitBlockId}, body blocks=[{string.Join(", ", _loops[headerBlockId].BodyBlockIds)}]");

        var headerBlock = cfg.FindBlock(headerBlockId);
        if (headerBlock == null) return;

        var loopInfo = _loops[headerBlockId];

        if (headerBlock.TerminatorKind == IrTerminatorKind.ConditionalBranch &&
            headerBlock.Successors.Count >= 2)
        {
            var succ0 = headerBlock.Successors[0].Target;
            var succ1 = headerBlock.Successors[1].Target;

            bool succ0InLoop = loopInfo.BodyBlockIds.Contains(succ0.Id);
            bool succ1InLoop = loopInfo.BodyBlockIds.Contains(succ1.Id);

            AstExpression condExpr = _translator.BuildConditionExpr(headerBlock, ssa, propagator);

            if (ConsoleReporter.IsTracing)
                ConsoleReporter.Debug($"EmitLoop: header is conditional branch. condExpr={Rosetta.CodeGen.CSharpEmitter.EmitExpr(condExpr)}, succ0={succ0.Id} (inLoop={succ0InLoop}), succ1={succ1.Id} (inLoop={succ1InLoop})");

            int bodyEntryId, exitId;
            AstExpression loopCondition;

            if (succ0InLoop && !succ1InLoop)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"EmitLoop: succ0 ({succ0.Id}) in loop, succ1 ({succ1.Id}) exits");
                bodyEntryId = succ0.Id;
                exitId = succ1.Id;
                loopCondition = condExpr;
            }
            else if (!succ0InLoop && succ1InLoop)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"EmitLoop: succ0 ({succ0.Id}) exits, succ1 ({succ1.Id}) in loop");
                bodyEntryId = succ1.Id;
                exitId = succ0.Id;
                loopCondition = AstTranslator.NegateCondition(condExpr);
            }
            else if (succ0InLoop && succ1InLoop)
            {
                var loopBody = new AstBlock();
                _translator.EmitStatements(headerBlockId, propagator, loopBody);

                var thenBody = new AstBlock();
                var elseBody = new AstBlock();
                int innerMerge = FindMergePoint(succ0.Id, succ1.Id, cfg, headerBlockId, ssa);
                var loopStop = new HashSet<int> { headerBlockId };
                if (loopInfo.ExitBlockId >= 0) loopStop.Add(loopInfo.ExitBlockId);
                if (outerStopAt != null) foreach (var s in outerStopAt) loopStop.Add(s);
                if (innerMerge >= 0 && loopInfo.BodyBlockIds.Contains(innerMerge) && innerMerge != headerBlockId)
                {
                    loopStop.Add(innerMerge);
                }

                if (visited.Add(succ0.Id))
                    EmitBlock(succ0.Id, cfg, ssa, propagator, thenBody, visited, loopStop);
                if (visited.Add(succ1.Id))
                    EmitBlock(succ1.Id, cfg, ssa, propagator, elseBody, visited, loopStop);

                if (thenBody.Statements.Count == 0 && elseBody.Statements.Count == 0)
                {
                    // Both branches inside the loop are empty; do not emit the if statement.
                }
                else if (thenBody.Statements.Count == 0 && elseBody.Statements.Count > 0)
                {
                    loopBody.Statements.Add(new AstIfStatement
                    {
                        Condition = AstTranslator.NegateCondition(condExpr),
                        ThenBody = elseBody
                    });
                }
                else
                {
                    loopBody.Statements.Add(new AstIfStatement
                    {
                        Condition = condExpr,
                        ThenBody = thenBody,
                        ElseBody = elseBody.Statements.Count > 0 ? elseBody : null
                    });
                }

                if (innerMerge >= 0 && loopInfo.BodyBlockIds.Contains(innerMerge) && innerMerge != headerBlockId && visited.Add(innerMerge))
                {
                    var innerStop = outerStopAt != null ? new HashSet<int>(outerStopAt) { headerBlockId } : new HashSet<int> { headerBlockId };
                    if (loopInfo.ExitBlockId >= 0) innerStop.Add(loopInfo.ExitBlockId);
                    EmitBlock(innerMerge, cfg, ssa, propagator, loopBody, visited, innerStop);
                }

                output.Statements.Add(new AstWhileStatement
                {
                    Condition = new AstLiteral { Value = true },
                    Body = loopBody
                });

                if (loopInfo.ExitBlockId >= 0 && visited.Add(loopInfo.ExitBlockId))
                {
                    EmitBlock(loopInfo.ExitBlockId, cfg, ssa, propagator, output, visited, outerStopAt);
                }
                return;
            }
            else
            {
                _translator.EmitStatements(headerBlockId, propagator, output);
                return;
            }

            bool isSelfLoop = (bodyEntryId == headerBlockId);

            if (isSelfLoop)
            {
                var loopBody = new AstBlock();
                _translator.EmitStatements(headerBlockId, propagator, loopBody);

                loopBody.Statements.Add(new AstIfStatement
                {
                    Condition = AstTranslator.NegateCondition(loopCondition),
                    ThenBody = new AstBlock { Statements = { new AstBreakStatement() } }
                });

                output.Statements.Add(new AstWhileStatement
                {
                    Condition = new AstLiteral { Value = true },
                    Body = loopBody
                });
            }
            else
            {
                var headerStmts = new AstBlock();
                _translator.EmitStatements(headerBlockId, propagator, headerStmts);

                var loopBody = new AstBlock();

                var loopStop = new HashSet<int> { headerBlockId };
                if (loopInfo.ExitBlockId >= 0) loopStop.Add(loopInfo.ExitBlockId);
                if (outerStopAt != null) foreach (var s in outerStopAt) loopStop.Add(s);

                if (visited.Add(bodyEntryId))
                    EmitBlock(bodyEntryId, cfg, ssa, propagator, loopBody, visited, loopStop);

                // Declare phis at merge points after the loop exit.
                // When the exit block chains (via fallthrough) to a phi-bearing block
                // whose phis receive values from both inside the loop body and the exit
                // path, those declarations must be at the enclosing scope — before the
                // while statement — so the variable is visible in both locations.
                // This mirrors the phi declaration logic in EmitBlock (lines 147-158)
                // which only handles non-loop conditional branches.
                {
                    var checkBlock = cfg.FindBlock(exitId);
                    var seen = new HashSet<int>();
                    while (checkBlock != null && seen.Add(checkBlock.Id))
                    {
                        var phis = ssa.GetPhis(checkBlock.Id);
                        if (phis.Count > 0 && !IsLoopHeader(checkBlock.Id, cfg))
                        {
                            foreach (var phi in phis)
                            {
                                if (phi.Destination.IsStackSlot && propagator.Ctx.CalleeSavedSpillSlots.Contains(phi.Destination.VarId - 200))
                                {
                                    continue;
                                }
                                string typeName = _translator.InferPhiType(phi.Destination, propagator, cfg);
                                output.Statements.Add(new AstVariableDeclaration
                                {
                                    TypeName = typeName,
                                    VarName = propagator.GetVarName(phi.Destination)
                                });
                            }
                            break;
                        }
                        if (checkBlock.Successors.Count != 1) break;
                        checkBlock = cfg.FindBlock(checkBlock.Successors[0].Target.Id);
                    }
                }

                if (headerStmts.Statements.Count == 0)
                {
                    output.Statements.Add(new AstWhileStatement
                    {
                        Condition = loopCondition,
                        Body = loopBody
                    });
                }
                else
                {
                    var fullBody = headerStmts;
                    fullBody.Statements.Add(new AstIfStatement
                    {
                        Condition = AstTranslator.NegateCondition(loopCondition),
                        ThenBody = new AstBlock { Statements = { new AstBreakStatement() } }
                    });
                    foreach (var stmt in loopBody.Statements)
                        fullBody.Statements.Add(stmt);

                    output.Statements.Add(new AstWhileStatement
                    {
                        Condition = new AstLiteral { Value = true },
                        Body = fullBody
                    });
                }
            }

            if (visited.Add(exitId))
            {
                EmitBlock(exitId, cfg, ssa, propagator, output, visited, outerStopAt);
            }
        }
        else if (headerBlock.Successors.Count == 1)
        {
            var loopBody = new AstBlock();
            _translator.EmitStatements(headerBlockId, propagator, loopBody);

            var succ = headerBlock.Successors[0].Target;
            if (succ.Id != headerBlockId && loopInfo.BodyBlockIds.Contains(succ.Id))
            {
                var loopStop = new HashSet<int> { headerBlockId };
                if (loopInfo.ExitBlockId >= 0) loopStop.Add(loopInfo.ExitBlockId);
                if (outerStopAt != null) foreach (var s in outerStopAt) loopStop.Add(s);

                if (visited.Add(succ.Id))
                    EmitBlock(succ.Id, cfg, ssa, propagator, loopBody, visited, loopStop);
            }

            output.Statements.Add(new AstWhileStatement
            {
                Condition = new AstLiteral { Value = true },
                Body = loopBody
            });

            if (loopInfo.ExitBlockId >= 0 && visited.Add(loopInfo.ExitBlockId))
            {
                EmitBlock(loopInfo.ExitBlockId, cfg, ssa, propagator, output, visited, outerStopAt);
            }
        }
        else
        {
            _translator.EmitStatements(headerBlockId, propagator, output);
        }
    }

}
