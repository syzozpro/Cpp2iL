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

public class AstTranslator
{
    public AstExpression? LastReturnExpr { get; set; }
    private AstBlock? _rootBlock;

    public AstTranslator(AstBlock? rootBlock)
    {
        _rootBlock = rootBlock;
    }

    public void EmitStatements(int blockId, ExprPropagator propagator, AstBlock output)
    {
        if (!propagator.BlockStatements.TryGetValue(blockId, out var stmts))
            return;

        foreach (var stmt in stmts)
        {
            if (stmt.SsaVar.HasValue && propagator.Inlined.Contains(stmt.SsaVar.Value))
                continue;

            // Skip empty expressions
            if (stmt.Expr is ExprVar ev && string.IsNullOrWhiteSpace(ev.Name))
                continue;

            // Return value: store typed expression for the block terminator handler
            if (stmt.IsReturn)
            {
                LastReturnExpr = TranslateExpr(stmt.Expr, propagator.Ctx.SpSlotNames, propagator.Ctx.Ssa, propagator);
                continue;
            }

            // Typed declaration: emit AstVariableDeclaration instead of string-flattened assignment
            if (stmt.IsDeclaration && stmt.Expr is ExprAssign declAssign)
            {
                string typeName = InferType(declAssign.Value, stmt.Inst);

                bool isResolved = false;

                if (declAssign.Target is ExprSpSlot spSlot && propagator.Ctx.SpSlotTypes.TryGetValue(spSlot.Offset, out var explicitType))
                {
                    typeName = explicitType;
                    isResolved = true;
                }
                else if (typeName != "var" && typeName != "int")
                {
                    isResolved = true;
                }

                string varName = declAssign.Target switch
                {
                    ExprVar tv => tv.Name,
                    ExprField ef => ef.Target is ExprVar efv ? $"{efv.Name}.{ef.FieldName}" : declAssign.Target.Emit(),
                    ExprSpSlot sp => (sp.Offset >= 0 && sp.Offset / 8 < propagator.Ctx.SpSlotNames.Length && propagator.Ctx.SpSlotNames[sp.Offset / 8] != null) ? propagator.Ctx.SpSlotNames[sp.Offset / 8] : $"local_sp{sp.Offset:X}",
                    _ => declAssign.Target.Emit()
                };
                output.Statements.Add(new AstVariableDeclaration
                {
                    TypeName = typeName,
                    VarName = varName,
                    IsTypeResolved = isResolved,
                    Initializer = TranslateExpr(declAssign.Value, propagator.Ctx.SpSlotNames, propagator.Ctx.Ssa, propagator)
                });
                continue;
            }

            // Normal statement: translate ExprNode → AstExpression structurally
            AstExpression astExpr = TranslateExpr(stmt.Expr, propagator.Ctx.SpSlotNames, propagator.Ctx.Ssa, propagator);

            // Retroactive Stack Type Update
            if (stmt.Expr is ExprAssign normalAssign && normalAssign.Target is ExprSpSlot tgtSpSlot)
            {
                if (_rootBlock != null)
                {
                    string searchName = (tgtSpSlot.Offset >= 0 && tgtSpSlot.Offset / 8 < propagator.Ctx.SpSlotNames.Length && propagator.Ctx.SpSlotNames[tgtSpSlot.Offset / 8] != null) ? propagator.Ctx.SpSlotNames[tgtSpSlot.Offset / 8] : tgtSpSlot.Emit();
                    var decl = _rootBlock.Statements.OfType<AstVariableDeclaration>().FirstOrDefault(d => d.VarName == searchName);
                    if (decl != null && !decl.IsTypeResolved)
                    {
                        if (!(normalAssign.Value is ExprLiteral lit && (lit.Value == null || (lit.Value is int i && i == 0))))
                        {
                            string newType = InferType(normalAssign.Value, stmt.Inst);
                            
                            // If RHS is a variable, its type might be declared earlier
                            if (newType == "var" && normalAssign.Value is ExprVar rhsVar)
                            {
                                var rhsDecl = output.Statements.OfType<AstVariableDeclaration>().FirstOrDefault(d => d.VarName == rhsVar.Name)
                                            ?? _rootBlock.Statements.OfType<AstVariableDeclaration>().FirstOrDefault(d => d.VarName == rhsVar.Name);
                                if (rhsDecl != null && rhsDecl.TypeName != "var")
                                {
                                    newType = rhsDecl.TypeName;
                                }
                            }

                            if (newType != "var" && newType != "int")
                            {
                                decl.TypeName = newType;
                                decl.IsTypeResolved = true;
                                if (decl.Initializer is AstLiteral initLit && 
                                    ((initLit.Value is int iVal && iVal == 0) || (initLit.Value is long lVal && lVal == 0)))
                                {
                                    decl.Initializer = null;
                                }
                            }
                        }
                    }
                }
            }

            var astStmt = new AstExpressionStatement
            {
                Expression = astExpr,
                Tag = ClassifyStatementTag(stmt)
            };
            output.Statements.Add(astStmt);
        }
    }

    public string InferType(ExprNode value, IrInstruction? inst = null, ExprPropagator? propagator = null, HashSet<string>? visited = null)
    {
        if (inst?.ResultType != null)
        {
            string typeName = CleanMetadataTypeName(inst.ResultType);
            if (Rosetta.Common.TypeUtils.IsGenericPlaceholder(typeName) && value is ExprCall call)
            {
                string resolved = Rosetta.Common.TypeUtils.ResolveGenericType(typeName, call.MethodName);
                if (resolved != typeName) return resolved;
            }
            return typeName;
        }

        if (!string.IsNullOrWhiteSpace(value.InferredType))
            return CleanMetadataTypeName(value.InferredType);

        if (value is ExprVar v)
        {
            if (ExprUtils.IsFloatLiteral(v)) return "float";
            if (ExprUtils.IsDoubleLiteral(v)) return "double";
            if (v.Name.StartsWith("\"") || v.Name.StartsWith("'")) return "string";

            int dotIdx = v.Name.IndexOf('.');
            if (dotIdx > 0 && !v.Name.Contains(' ') && !v.Name.Contains('('))
            {
                return v.Name.Substring(0, dotIdx);
            }
        }

        if (value is ExprLiteral lit)
        {
            return lit.Value switch
            {
                int => "int",
                long l when l >= int.MinValue && l <= int.MaxValue => "int",
                long => "long",
                float => "float",
                double => "double",
                bool => "bool",
                string => "string",
                null => "var",
                _ => "var"
            };
        }

        if (value is ExprCast cast) return cast.TypeName;
        if (value is ExprTypeOf) return "Type";

        if (value is ExprNew ne)
        {
            if (ne.Size != null || ne.Initializer != null)
                return ne.IsMultiDim ? ne.TypeName + "[,]" : ne.TypeName + "[]";
            return ne.TypeName;
        }

        if (value is ExprBinary binary)
        {
            switch (binary.Op)
            {
                case "==": case "!=":
                case "<": case ">":
                case "<=": case ">=":
                case "&&": case "||":
                    return "bool";
                case "+": case "-":
                case "*": case "/":
                case "%":
                case "<<": case ">>":
                case "&": case "|": case "^":
                    string leftType = InferType(binary.Left, null, propagator, visited);
                    string rightType = InferType(binary.Right, null, propagator, visited);
                    
                    bool leftIsPrimitive = leftType == "float" || leftType == "int" || leftType == "double" || leftType == "var";
                    bool rightIsPrimitive = rightType == "float" || rightType == "int" || rightType == "double" || rightType == "var";

                    if (!leftIsPrimitive) return leftType;
                    if (!rightIsPrimitive) return rightType;
                    if (leftType != "var") return leftType;
                    if (rightType != "var") return rightType;
                    break;
            }
        }

        if (value is ExprUnary unary && unary.Op == "!") return "bool";

        if (propagator != null)
        {
            if (value is ExprVar ev) return ResolveVarType(ev.Name, propagator, visited);
            if (value is ExprSpSlot spSlot) return ResolveVarType(spSlot.Emit(), propagator, visited);
        }

        return "var";
    }

    public static StatementTag ClassifyStatementTag(ExprStatement stmt)
    {
        var expr = stmt.Expr;
        var annotation = stmt.Inst?.Annotation;

        if (expr is ExprAssign { Value: ExprTypeOf }) return StatementTag.TypeOf;
        if (expr is ExprTypeOf) return StatementTag.TypeOf;

        if (expr is ExprMethodRef) return StatementTag.MethodRef;
        if (expr is ExprAssign { Value: ExprMethodRef }) return StatementTag.MethodRef;

        if (expr is ExprClassInit) return StatementTag.ClassInitFlag;
        if (expr is ExprAssign { Value: ExprClassInit }) return StatementTag.ClassInitFlag;

        if (annotation == "metadata_var") return StatementTag.MetadataVar;
        if (annotation != null && annotation == "il2cpp_metadata_page") return StatementTag.MetadataPageStore;

        if (stmt.Inst?.SemanticTag == IrSemanticTag.VTableLoad || annotation == "static_fields")
            return StatementTag.VTableLoad;

        return StatementTag.None;
    }

    public static string CleanMetadataTypeName(string typeName) => Rosetta.Common.TypeUtils.CleanTypeName(typeName);

    public static AstExpression TranslateExpr(ExprNode expr, string[]? spSlotNames = null, SsaContext? ssa = null, ExprPropagator? propagator = null)
    {
        return expr switch
        {
            ExprAssign assign => TranslateAssign(assign, spSlotNames, ssa, propagator),
            ExprCall call => new AstCallExpression { MethodName = call.MethodName, Target = call.Target != null ? TranslateExpr(call.Target, spSlotNames, ssa, propagator) : null, Arguments = call.Args.ConvertAll(arg => TranslateExpr(arg, spSlotNames, ssa, propagator)) },
            ExprBinary bin => TranslateBinaryExpr(bin, spSlotNames, ssa, propagator),
            ExprUnary un => un.Op.Length >= 2 && un.Op[0] == '(' && un.Op[^1] == ')' ? new AstCastExpression { TypeName = un.Op[1..^1], Operand = TranslateExpr(un.Operand, spSlotNames, ssa, propagator) } : new AstUnaryExpression { Operator = un.Op, Operand = TranslateExpr(un.Operand, spSlotNames, ssa, propagator) },
            ExprCast cast => new AstCastExpression { TypeName = cast.TypeName, Operand = TranslateExpr(cast.Operand, spSlotNames, ssa, propagator) },
            ExprField field => TranslateField(field, spSlotNames, ssa, propagator),
            ExprIndex idx => new AstIndexAccess { Target = TranslateExpr(idx.Target, spSlotNames, ssa, propagator), Index = TranslateExpr(idx.Index, spSlotNames, ssa, propagator) },
            ExprNew ne when ne.Size != null => new AstNewExpression { TypeName = ne.IsMultiDim ? ne.TypeName + "[,]" : ne.TypeName + "[]", Arguments = new List<AstExpression> { TranslateExpr(ne.Size, spSlotNames, ssa, propagator) }, Initializer = ne.Initializer },
            ExprNew ne => new AstNewExpression { TypeName = ne.TypeName, Arguments = ne.Args.ConvertAll(arg => TranslateExpr(arg, spSlotNames, ssa, propagator)), Initializer = ne.Initializer },
            ExprStructInit si => new AstNewExpression { TypeName = si.TypeName, Arguments = si.Fields.Select(f => { AstExpression val = TranslateExpr(f.Value, spSlotNames, ssa, propagator); return (AstExpression)new AstAssignment { Target = new AstIdentifier { Name = f.FieldName }, Value = val }; }).ToList() },
            ExprLiteral lit => new AstLiteral { Value = lit.Value },
            ExprTypeOf typeOf => new AstCallExpression { MethodName = "typeof", Arguments = new List<AstExpression> { new AstIdentifier { Name = typeOf.TypeName } } },
            ExprClassInit classInit => new AstCallExpression { MethodName = "class_init", Arguments = new List<AstExpression> { new AstIdentifier { Name = classInit.TypeName } } },
            ExprVTableAccess vta => new AstIndexAccess { Target = new AstIdentifier { Name = $"vtable<{vta.TypeName}>" }, Index = new AstLiteral { Value = vta.SlotIndex } },
            ExprTernary ternary => new AstBinaryExpression { Operator = "?", Left = TranslateExpr(ternary.Condition, spSlotNames, ssa, propagator), Right = new AstBinaryExpression { Operator = ":", Left = TranslateExpr(ternary.TrueValue, spSlotNames, ssa, propagator), Right = TranslateExpr(ternary.FalseValue, spSlotNames, ssa, propagator) } },
            ExprOut outExpr => new AstUnaryExpression { Operator = "out ", Operand = new AstIdentifier { Name = outExpr.VarName } },
            ExprRef refExpr => new AstUnaryExpression { Operator = "ref ", Operand = new AstIdentifier { Name = refExpr.VarName } },
            ExprThis => new AstIdentifier { Name = "this" },
            ExprSpSlot sp => new AstIdentifier { Name = (spSlotNames != null && sp.Offset >= 0 && sp.Offset / 8 < spSlotNames.Length && spSlotNames[sp.Offset / 8] != null) ? spSlotNames[sp.Offset / 8] : $"local_sp{sp.Offset:X}" },
            ExprVar v => new AstIdentifier 
            { 
                Name = v.Name, 
                SsaVar = ssa?.AllVariables.FirstOrDefault(x => x.VarId == v.VarId && x.Version == v.Version) 
                         ?? new SsaVariable(v.VarId, v.Version, 0, v.ElementWidth, v.ElementCount)
            },
            ExprMemory mem => new AstMemoryAccess { Base = TranslateExpr(mem.Base, spSlotNames, ssa, propagator), Offset = mem.Offset },
            _ => new AstIdentifier { Name = expr.Emit() }
        };
    }

    private static AstExpression TranslateField(ExprField field, string[]? spSlotNames, SsaContext? ssa, ExprPropagator? propagator)
    {
        string memberName = field.FieldName;
        bool isProp = field.IsProperty;
        if (propagator != null && (memberName == "_size" || memberName == "_count" || memberName == "size" || memberName == "count") && field.Target is ExprVar targetVar)
        {
            string targetType = new AstTranslator(null).ResolveVarType(targetVar.Name, propagator);
            if (Rosetta.Common.TypeUtils.IsStandardCollectionType(targetType))
            {
                memberName = "Count";
                isProp = true;
            }
        }
        return new AstMemberAccess { Target = TranslateExpr(field.Target, spSlotNames, ssa, propagator), MemberName = memberName, IsProperty = isProp };
    }

    private static AstExpression TranslateBinaryExpr(ExprBinary bin, string[]? spSlotNames, SsaContext? ssa, ExprPropagator? propagator)
    {
        var left = TranslateExpr(bin.Left, spSlotNames, ssa, propagator);
        var right = TranslateExpr(bin.Right, spSlotNames, ssa, propagator);

        if ((bin.Op == "==" || bin.Op == "!=") && right is AstLiteral lit && (lit.Value is int val && val == 0 || lit.Value is long lVal && lVal == 0))
        {
            if (propagator != null && bin.Left is not ExprBinary && bin.Left is not ExprLiteral)
            {
                string typeName = new AstTranslator(null).InferType(bin.Left, null, propagator);
                var typeEnum = Rosetta.Analysis.Utils.TypeUtils.TypeHintToElementType(typeName);
                
                bool isValueType = typeEnum == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN ||
                                   (typeEnum >= Il2CppTypeEnum.IL2CPP_TYPE_CHAR && typeEnum <= Il2CppTypeEnum.IL2CPP_TYPE_R8) ||
                                   typeEnum == Il2CppTypeEnum.IL2CPP_TYPE_I || typeEnum == Il2CppTypeEnum.IL2CPP_TYPE_U;
                
                if (!isValueType && typeName != "var" && typeName != "int" && typeName != "float" && typeName != "double" && typeName != "long" && typeName != "bool")
                {
                    right = new AstLiteral { Value = null };
                }
            }
        }

        return new AstBinaryExpression { Operator = bin.Op, Left = left, Right = right };
    }

    private static AstExpression TranslateAssign(ExprAssign assign, string[]? spSlotNames, SsaContext? ssa, ExprPropagator? propagator)
    {
        var targetExpr = TranslateExpr(assign.Target, spSlotNames, ssa, propagator);
        var valueExpr = TranslateExpr(assign.Value, spSlotNames, ssa, propagator);

        return new AstAssignment { Target = targetExpr, Value = valueExpr };
    }

    public AstExpression BuildConditionExpr(IrBasicBlock block, SsaContext ssa, ExprPropagator propagator)
    {
        var branch = block.Instructions.LastOrDefault(i => i.Opcode == IrOpcode.ConditionalBranch);
        if (branch == null) return new AstLiteral { Value = true };

        string? annotation = branch.Annotation;

        if (branch.Sources.Length > 0 && branch.Sources[0].Kind == IrOperandKind.Register)
        {
            ExprNode regNode = GetOperandNode(branch, 0, ssa, propagator);
            string typeName = InferType(regNode, branch, propagator);
            AstExpression regExpr = TranslateExpr(regNode, propagator.Ctx.SpSlotNames, ssa, propagator);

            var typeEnum = Rosetta.Analysis.Utils.TypeUtils.TypeHintToElementType(typeName);
            bool isBool = typeEnum == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN;
            bool isNumeric = typeEnum is >= Il2CppTypeEnum.IL2CPP_TYPE_CHAR and <= Il2CppTypeEnum.IL2CPP_TYPE_R8
                             || typeEnum is Il2CppTypeEnum.IL2CPP_TYPE_I or Il2CppTypeEnum.IL2CPP_TYPE_U;

            if (isBool)
            {
                if (branch.Condition == IrBranchCondition.BitZero || branch.Condition == IrBranchCondition.Zero)
                    return NegateCondition(regExpr);
                return TruthifyCondition(regExpr);
            }
            else if (isNumeric)
            {
                string op = (branch.Condition == IrBranchCondition.Zero || branch.Condition == IrBranchCondition.BitZero) ? "==" : "!=";
                return new AstBinaryExpression { Operator = op, Left = regExpr, Right = new AstLiteral { Value = 0 } };
            }
            else
            {
                string op = (branch.Condition == IrBranchCondition.Zero || branch.Condition == IrBranchCondition.BitZero) ? "==" : "!=";
                return new AstBinaryExpression { Operator = op, Left = regExpr, Right = new AstLiteral { Value = null } };
            }
        }

        string condCode = "!=";
        if (branch.Sources.Length > 0 && branch.Sources[0].Kind == IrOperandKind.Condition)
        {
            condCode = Rosetta.Analysis.Utils.OpUtils.ConditionToOperator(branch.Sources[0].Name);
        }

        var compare = block.Instructions.LastOrDefault(i => i.Opcode is IrOpcode.Compare or IrOpcode.FCompare or IrOpcode.Test
            || (i.Opcode is IrOpcode.Sub && i.Annotation == "sets flags"));

        if (compare != null)
        {
            int leftIdx = 0;
            int rightIdx = 1;
            if (compare.Sources.Length >= 3 && compare.Sources[0].Kind == IrOperandKind.Condition)
            {
                leftIdx = 1;
                rightIdx = 2;
            }
            var leftNode = GetOperandNode(compare, leftIdx, ssa, propagator);
            var rightNode = GetOperandNode(compare, rightIdx, ssa, propagator);
            CoerceEnumConditionOperands(ref leftNode, ref rightNode, propagator);

            var left = TranslateExpr(leftNode, propagator.Ctx.SpSlotNames, ssa, propagator);
            var right = TranslateExpr(rightNode, propagator.Ctx.SpSlotNames, ssa, propagator);

            if (compare.Opcode == IrOpcode.Test)
            {
                // test reg, reg → null check pattern: simplify (reg & reg) to just reg
                if (left is AstIdentifier lid && right is AstIdentifier rid && lid.Name == rid.Name)
                {
                    string op = condCode == "!=" ? "!=" : condCode == "==" ? "==" : condCode;
                    return new AstBinaryExpression { Operator = op, Left = left, Right = new AstLiteral { Value = null } };
                }

                bool isRightOne = right is AstLiteral lit && (lit.Value is int i && i == 1 || lit.Value is long l && l == 1 || lit.Value is byte b && b == 1);
                string leftType = InferType(leftNode, compare, propagator);
                bool isLeftBool = leftType == "bool" || leftType == "System.Boolean" ||
                                  (leftNode is ExprBinary binExpr && (binExpr.Op == "==" || binExpr.Op == "!=" || binExpr.Op == "<" || binExpr.Op == ">" || binExpr.Op == "<=" || binExpr.Op == ">=")) ||
                                  (leftNode is ExprUnary unExpr && unExpr.Op == "!");

                if (isRightOne && isLeftBool)
                {
                    if (condCode == "==") return NegateCondition(left);
                    return TruthifyCondition(left);
                }

                var bitAnd = new AstBinaryExpression { Operator = "&", Left = left, Right = right };
                return new AstBinaryExpression { Operator = condCode == "!=" ? "!=" : condCode == "==" ? "==" : condCode, Left = bitAnd, Right = new AstLiteral { Value = 0 } };
            }

            if (AstUtils.IsZeroOrNull(right))
            {
                if (condCode == "==") return NegateCondition(left);
                if (condCode == "!=") return left;
            }

            return new AstBinaryExpression { Operator = condCode, Left = left, Right = right };
        }

        return new AstLiteral { Value = true };
    }

    private static void CoerceEnumConditionOperands(ref ExprNode left, ref ExprNode right, ExprPropagator propagator)
    {
        int typeDefIndex = left.MetadataTypeDefIndex >= 0
            ? left.MetadataTypeDefIndex
            : right.MetadataTypeDefIndex;
        if (typeDefIndex < 0)
            return;

        left = CoerceEnumLiteral(left, typeDefIndex, propagator);
        right = CoerceEnumLiteral(right, typeDefIndex, propagator);
    }

    private static ExprNode CoerceEnumLiteral(ExprNode expr, int typeDefIndex, ExprPropagator propagator)
    {
        if (!TryGetIntegralLiteral(expr, out long value))
            return expr;

        string? literal = propagator.Ctx.TypeModel?.ResolveEnumLiteralByTypeDefIndex(typeDefIndex, value);
        if (literal == null)
            return expr;

        if (literal.Contains(" | "))
            literal = $"({literal})";

        return new ExprVar(literal) { MetadataTypeDefIndex = typeDefIndex };
    }

    private static bool TryGetIntegralLiteral(ExprNode expr, out long value)
    {
        value = 0;
        if (expr is not ExprLiteral lit)
            return false;

        switch (lit.Value)
        {
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case uint ui:
                value = ui;
                return true;
            case ulong ul when ul <= long.MaxValue:
                value = (long)ul;
                return true;
            case byte b:
                value = b;
                return true;
            case sbyte sb:
                value = sb;
                return true;
            case short s:
                value = s;
                return true;
            case ushort us:
                value = us;
                return true;
            default:
                return false;
        }
    }

    public static AstExpression TruthifyCondition(AstExpression expr)
    {
        if (expr is AstLiteral lit)
        {
            if (lit.Value is bool b) return new AstLiteral { Value = b };
            if (lit.Value is int i) return new AstLiteral { Value = i != 0 };
            if (lit.Value is long l) return new AstLiteral { Value = l != 0 };
            if (lit.Value is float f) return new AstLiteral { Value = f != 0f };
            if (lit.Value is double d) return new AstLiteral { Value = d != 0d };
            if (lit.Value == null) return new AstLiteral { Value = false };
        }
        else if (expr is AstIdentifier id)
        {
            if (id.Name == "true") return new AstLiteral { Value = true };
            if (id.Name == "false") return new AstLiteral { Value = false };
            if (id.Name == "0" || id.Name == "0f" || id.Name == "0d" || id.Name == "0L" || id.Name == "null") return new AstLiteral { Value = false };
            if (float.TryParse(id.Name.TrimEnd('f', 'd', 'L', 'u'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float result))
            {
                return new AstLiteral { Value = result != 0f };
            }
        }
        return expr;
    }

    public static AstExpression NegateCondition(AstExpression expr)
    {
        if (expr is AstLiteral lit)
        {
            if (lit.Value is bool b) return new AstLiteral { Value = !b };
            if (lit.Value is int i) return new AstLiteral { Value = i == 0 };
            if (lit.Value is long l) return new AstLiteral { Value = l == 0 };
            if (lit.Value is float f) return new AstLiteral { Value = f == 0f };
            if (lit.Value is double d) return new AstLiteral { Value = d == 0d };
            if (lit.Value == null) return new AstLiteral { Value = true };
        }
        else if (expr is AstIdentifier id)
        {
            if (id.Name == "true") return new AstLiteral { Value = false };
            if (id.Name == "false") return new AstLiteral { Value = true };
            if (id.Name == "0" || id.Name == "0f" || id.Name == "0d" || id.Name == "0L" || id.Name == "null") return new AstLiteral { Value = true };
            if (float.TryParse(id.Name.TrimEnd('f', 'd', 'L', 'u'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float result))
            {
                return new AstLiteral { Value = result == 0f };
            }
        }

        if (expr is AstUnaryExpression { Operator: "!" } un) return un.Operand;
        return new AstUnaryExpression { Operator = "!", Operand = expr };
    }

    public AstExpression GetOperandExpr(IrInstruction inst, int srcIdx, SsaContext ssa, ExprPropagator propagator)
        => TranslateExpr(GetOperandNode(inst, srcIdx, ssa, propagator), propagator.Ctx.SpSlotNames, ssa, propagator);

    private ExprNode GetOperandNode(IrInstruction inst, int srcIdx, SsaContext ssa, ExprPropagator propagator)
    {
        if (srcIdx >= inst.Sources.Length) return new ExprVar("?");

        var ssaVar = ssa.GetSource(inst.Address, srcIdx);
        if (ssaVar.HasValue)
            return propagator.Resolve(ssaVar.Value);

        var src = inst.Sources[srcIdx];
        return src.Kind switch
        {
            IrOperandKind.Immediate => src.Value switch
            {
                0 => new ExprLiteral(0),
                <= int.MaxValue and >= int.MinValue => new ExprLiteral((int)src.Value),
                <= uint.MaxValue and >= 0 => new ExprVar($"0x{src.Value:X}"),
                _ => new ExprVar($"0x{src.Value:X}")
            },
            IrOperandKind.FloatImmediate => FormatFloatAsExprLiteral(src.Value, src.BitWidth),
            IrOperandKind.Register => new ExprVar(
                propagator.Method.IsArm32
                    ? (src.Value == 13 ? "SP" : src.Value == 15 ? "PC" : src.Value == 14 ? "LR" : $"R{src.Value}")
                    : (src.Value == 31 ? "SP" : $"x{src.Value}")),
            IrOperandKind.FpRegister => new ExprVar($"s{src.Value}"),
            IrOperandKind.Memory => new ExprMemory(
                new ExprVar(
                    propagator.Method.IsArm32
                        ? (src.Value == 13 ? "SP" : src.Value == 15 ? "PC" : src.Value == 14 ? "LR" : $"R{src.Value}")
                        : (src.Value == 31 ? "SP" : $"x{src.Value}")),
                src.Offset),
            _ => new ExprVar("?")
        };
    }

    private static ExprLiteral FormatFloatAsExprLiteral(long bits, byte bitWidth)
    {
        if (bits == 0) return new ExprLiteral(0);
        if (bitWidth <= 32)
        {
            float f = System.BitConverter.ToSingle(System.BitConverter.GetBytes((int)bits));
            return new ExprLiteral(f);
        }
        double d = System.BitConverter.Int64BitsToDouble(bits);
        return new ExprLiteral(d);
    }

    public static AstExpression FormatFloatAsAstLiteral(long bits, byte bitWidth)
    {
        if (bits == 0) return new AstLiteral { Value = (int)0 };
        if (bitWidth <= 32)
        {
            float f = System.BitConverter.ToSingle(System.BitConverter.GetBytes((int)bits));
            return new AstLiteral { Value = f };
        }
        double d = System.BitConverter.Int64BitsToDouble(bits);
        return new AstLiteral { Value = d };
    }

    public string InferPhiType(SsaVariable phiVar, ExprPropagator propagator, IrControlFlowGraph cfg)
    {
        var visited = new HashSet<string>();
        foreach (var pair in propagator.BlockStatements)
        {
            foreach (var stmt in pair.Value)
            {
                if (stmt.Expr is ExprAssign assign && assign.Target is ExprVar targetVar && targetVar.VarId == phiVar.VarId && targetVar.Version == phiVar.Version)
                {
                    string t = InferType(assign.Value, stmt.Inst, propagator, visited);
                    if (t != "var") return t;
                }
            }
        }
        return "object";
    }

    public string ResolveVarType(string varName, ExprPropagator propagator, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>();
        if (!visited.Add(varName)) return "var";

        foreach (var pair in propagator.BlockStatements)
        {
            foreach (var stmt in pair.Value)
            {
                if (stmt.IsDeclaration && stmt.Expr is ExprAssign assign)
                {
                    bool match = false;
                    if (assign.Target is ExprVar targetVar && targetVar.Name == varName) match = true;
                    else if (assign.Target is ExprSpSlot spSlot && spSlot.Emit() == varName) match = true;

                    if (match)
                    {
                        string t = InferType(assign.Value, stmt.Inst, propagator, visited);
                        if (t != "var") return t;
                    }
                }
            }
        }
        return "var";
    }
}