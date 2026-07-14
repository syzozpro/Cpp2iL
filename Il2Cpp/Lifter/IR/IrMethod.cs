using Rosetta.Lifter.IR.Nodes;

namespace Rosetta.Lifter.IR;

/// <summary>
/// Container for a method's IR representation.
/// This is the output of <see cref="IrLifter"/> and the input to future CFG construction.
///
/// Scalability: When connecting to CFG, the Instructions list gets split into
/// BasicBlock&lt;IrInstruction&gt; objects using the same splitting logic as ARM64 blocks
/// (branch targets become block boundaries), but operating on IR opcodes instead.
/// </summary>
public sealed class IrMethod
{
    /// <summary>Method name from metadata.</summary>
    public string MethodName { get; init; } = "";

    /// <summary>Declaring type name (fully qualified).</summary>
    public string? DeclaringType { get; init; }

    /// <summary>Whether this method is lifted from a 32-bit ARM (Thumb2) binary.</summary>
    public bool IsArm32 { get; init; }

    /// <summary>Return type name from metadata.</summary>
    public string ReturnType { get; init; } = "void";

    /// <summary>Parameter signatures (type + name pairs).</summary>
    public List<string> Parameters { get; init; } = [];

    /// <summary>Whether this method is static (no 'this' pointer in X0).</summary>
    public bool IsStatic { get; init; }

    /// <summary>Original ARM64 entry point virtual address.</summary>
    public ulong EntryAddress { get; init; }

    /// <summary>Metadata token (for cross-referencing).</summary>
    public uint Token { get; init; }

    /// <summary>TypeDefinition index of the declaring type (for field resolution).</summary>
    public int TypeDefIndex { get; init; } = -1;

    /// <summary>Global index of this method in the metadata MethodDefinitions array.</summary>
    public int MethodIndex { get; init; } = -1;

    /// <summary>
    /// The lifted IR instructions, in program order.
    /// This list preserves the original instruction ordering and is ready
    /// for basic-block splitting using Branch/ConditionalBranch/Return opcodes.
    /// </summary>
    public List<IrInstruction> Instructions { get; init; } = [];

    /// <summary>
    /// Statistics: how many ARM64 instructions were in the original method body.
    /// </summary>
    public int OriginalInstructionCount { get; init; }

    /// <summary>
    /// Statistics: how many ARM64 instructions were collapsed/fused into fewer IR ops.
    /// </summary>
    public int CollapsedInstructionCount { get; set; }

    /// <summary>Map GP register index (x0-x7 / w0-w7) to parameter name.</summary>
    public System.Collections.Generic.Dictionary<int, string> GpParamMap { get; } = new();

    /// <summary>Map GP register index to the bare (cleaned) parameter type name for field resolution.</summary>
    public System.Collections.Generic.Dictionary<int, string> GpParamTypeMap { get; } = new();

    /// <summary>Set of GP register indices that hold `out` parameters (require full struct assignment).</summary>
    public System.Collections.Generic.HashSet<int> GpParamIsOut { get; } = new();

    /// <summary>Map FP register index (s0-s7 / d0-d7) to parameter name.</summary>
    public System.Collections.Generic.Dictionary<int, string> FpParamMap { get; } = new();

    /// <summary>
    /// Pre-build the register-to-parameter mappings once for efficient O(1) lookups during SSA resolution.
    /// </summary>
    public void BuildParamMaps()
    {
        GpParamMap.Clear();
        GpParamTypeMap.Clear();
        GpParamIsOut.Clear();
        FpParamMap.Clear();

        if (Parameters == null || Parameters.Count == 0) return;

        int gpReg = IsStatic ? 0 : 1;
        int fpReg = 0;

        foreach (var sig in Parameters)
        {
            int lastSpace = sig.LastIndexOf(' ');
            if (lastSpace < 0) continue;

            string typePart = sig[..lastSpace];
            string paramName = sig[(lastSpace + 1)..];

            string cleanType = Rosetta.Common.TypeUtils.CleanTypeName(typePart);
            bool paramIsFloat = cleanType is "float" or "double";

            // Strip ref/out/in/& qualifiers to get the bare type name
            string bareType = cleanType;
            if (bareType.StartsWith("in ")) bareType = bareType[3..];
            if (bareType.StartsWith("ref ")) bareType = bareType[4..];
            if (bareType.StartsWith("out ")) bareType = bareType[4..];
            if (bareType.EndsWith("&")) bareType = bareType[..^1];

            if (paramIsFloat)
            {
                FpParamMap[fpReg++] = paramName;
            }
            else
            {
                GpParamMap[gpReg] = paramName;
                GpParamTypeMap[gpReg] = bareType;
                if (cleanType.StartsWith("out ")) GpParamIsOut.Add(gpReg);
                gpReg++;
            }
        }
    }


    // ─── Display ────────────────────────────────────────────────────────────

    /// <summary>Format the entire method as an IR text block for dump output.</summary>
    public string ToDisplayString()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"; ───────────────────────────────────────────────────────────────");
        sb.AppendLine($"; Method: {ReturnType} {MethodName}({string.Join(", ", Parameters)})");
        if (DeclaringType != null)
            sb.AppendLine($"; Type: {DeclaringType}");
        sb.AppendLine($"; Address: 0x{EntryAddress:X}  Token: 0x{Token:X8}");
        sb.AppendLine($"; Stats: {OriginalInstructionCount} native → {Instructions.Count} IR ops ({CollapsedInstructionCount} collapsed)");
        sb.AppendLine($"; ───────────────────────────────────────────────────────────────");

        foreach (var ir in Instructions)
        {
            sb.AppendLine($"  [{ir.Address:X8}]  {ir}");
        }

        return sb.ToString();
    }
}
