using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Lifter.IR;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Model;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Groups homogeneous floating-point aggregate (HFA) register arguments (s0, s1, s2...)
/// into C# Vector structures (Vector2, Vector3, Vector4, Quaternion, Color).
/// Uses method signature parameter metadata from the TypeModel when available and matching,
/// and falls back to a register-run heuristic otherwise (to handle overloaded methods).
/// </summary>
public static class HfaArgumentGrouper
{
    public static void Group(List<ExprNode> args, IrInstruction inst, int firstArgSource,
        string methodName, IReadOnlyList<MethodSignature.ParamEntry>? parameters)
    {
        if (ConsoleReporter.Verbose)
        {
            ConsoleReporter.Debug($"HfaArgumentGrouper: Grouping args for method '{methodName}', argsCount={args.Count}, firstArgSource={firstArgSource}");
        }
        if (args.Count < 2) return;

        // Pre-compute which arguments originally came from FP registers
        // using the instruction's original sources to ensure we match even after SSA propagation.
        bool[] isFpArg = new bool[args.Count];
        for (int a = 0; a < args.Count; a++)
        {
            int srcIdx = firstArgSource + a;
            isFpArg[a] = srcIdx < inst.Sources.Length &&
                         inst.Sources[srcIdx].Kind == IrOperandKind.FpRegister;
        }

        int actualFpCount = isFpArg.Count(x => x);



        // PRIMARY PATH: Metadata-driven grouping
        if (parameters != null && parameters.Count > 0)
        {
            var fpParams = new List<(string Name, string TypeName, int HfaSize)>();
            int expectedFpCount = 0;
            foreach (var p in parameters)
            {
                string t = p.TypeName;
                if (t is "float" or "System.Single" or "double" or "System.Double")
                {
                    fpParams.Add((p.Name, t, 1));
                    expectedFpCount += 1;
                }
                else if (p.HfaSize >= 2)
                {
                    fpParams.Add((p.Name, t, p.HfaSize));
                    expectedFpCount += p.HfaSize;
                }
            }

            var fpArgIndices = new List<int>();
            for (int a = 0; a < args.Count; a++)
            {
                if (isFpArg[a]) fpArgIndices.Add(a);
            }

            int fpArgIndexOffset = 0;
            var replacements = new List<(int start, int count, string type)>();

            for (int pIdx = 0; pIdx < fpParams.Count; pIdx++)
            {
                var param = fpParams[pIdx];
                if (param.HfaSize >= 2)
                {
                    if (fpArgIndexOffset + param.HfaSize <= fpArgIndices.Count)
                    {
                        int actualStartIdx = fpArgIndices[fpArgIndexOffset];
                        replacements.Add((actualStartIdx, param.HfaSize, param.TypeName));
                    }
                }
                fpArgIndexOffset += param.HfaSize;
            }

            if (replacements.Count > 0)
            {
                if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"        HFA: using metadata for {methodName} ({parameters.Count} params)");

                // Apply replacements in reverse order to preserve indices
                for (int r = replacements.Count - 1; r >= 0; r--)
                {
                    var (gStart, gCount, gType) = replacements[r];
                    if (gType.StartsWith("UnityEngine.")) gType = gType["UnityEngine.".Length..];

                    var hfaArgs = args.GetRange(gStart, gCount);

                    // Structurally unpack SIMD and double arguments into floats
                    // since we know they are being packed into a float-based HFA structure.
                    for (int k = 0; k < hfaArgs.Count; k++)
                    {
                        if (hfaArgs[k] is ExprSimd simd)
                        {
                            var slots = simd.GetSlots32();
                            hfaArgs[k] = new ExprLiteral(BitConverter.Int32BitsToSingle((int)slots[0]));
                        }
                        else if (hfaArgs[k] is ExprLiteral lit && lit.Value is double dval)
                        {
                            hfaArgs[k] = new ExprLiteral((float)dval);
                        }
                    }

                    // Struct identity: new T(x.f0, x.f1, ..., x.fN) ≡ x
                    // When all components are field accesses of the same target variable,
                    // the struct is being passed through unchanged. This is the mathematical
                    // inverse of HFA decomposition — not a heuristic.
                    ExprNode? identity = TryStructIdentity(hfaArgs);

                    args.RemoveRange(gStart, gCount);
                    var replacedNode = identity ?? new ExprNew(gType, null, hfaArgs);
                    args.Insert(gStart, replacedNode);

                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Debug($"HfaArgumentGrouper: Replaced HFA register arguments at index {gStart} with: {replacedNode.Emit()}");
                    }
                }
                return;
            }
        }
    }

    /// <summary>
    /// Checks struct identity: if ALL components are ExprField accesses of the same target,
    /// the struct is passed through unchanged → return the target directly.
    /// new T(x.f0, x.f1, ..., x.fN) ≡ x  (for value types, by definition).
    ///
    /// Also handles SIMD lane decomposition: when the first N-1 args are the same ExprVar
    /// with ElementCount >= 2 (packed SIMD register), and the last arg is a separate scalar,
    /// the packed variable already represents the complete struct computation.
    /// This is the mathematical inverse of AAPCS HFA decomposition + SIMD lane aliasing.
    /// </summary>
    private static ExprNode? TryStructIdentity(List<ExprNode> hfaArgs)
    {
        if (hfaArgs.Count < 2) return null;
        var first = hfaArgs[0];

        if (first is ExprVar firstVar)
        {
            // Case 1: ALL args are the same ExprVar → direct passthrough
            bool allSame = true;
            for (int i = 1; i < hfaArgs.Count; i++)
            {
                if (!AreSameTarget(first, hfaArgs[i])) { allSame = false; break; }
            }
            if (allSame) return first;

            // Case 2: SIMD lane decomposition — first N-1 args are the same packed ExprVar.
            // When a SIMD operation (e.g., FMUL V0.2S) computes components {x,y} packed into
            // one d-register, SSA aliases s0 and the element-extracted s1 to the same variable.
            // The remaining arg (z) was computed by a parallel scalar operation.
            // The packed variable IS the complete struct — AAPCS just required decomposition.
            if (hfaArgs.Count >= 3 && firstVar.ElementCount >= 2)
            {
                bool prefixSame = true;
                for (int i = 1; i < hfaArgs.Count - 1; i++)
                {
                    if (!AreSameTarget(first, hfaArgs[i])) { prefixSame = false; break; }
                }
                if (prefixSame) return first;
            }
        }

        if (first is not ExprField firstField) return null;

        // Compare targets structurally: ExprVar by Name, ExprThis by type
        for (int i = 1; i < hfaArgs.Count; i++)
        {
            if (hfaArgs[i] is not ExprField f) return null;
            if (!AreSameTarget(firstField.Target, f.Target)) return null;
        }

        return firstField.Target;
    }

    /// <summary>Structurally compare two ExprNode targets for identity.</summary>
    private static bool AreSameTarget(ExprNode a, ExprNode b)
    {
        if (a is ExprVar va && b is ExprVar vb) return va.Name == vb.Name;
        if (a is ExprThis && b is ExprThis) return true;
        return false;
    }
}
