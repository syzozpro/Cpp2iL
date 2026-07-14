using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rosetta.Analysis.IR.DataFlow;
using Rosetta.Analysis.Resolve;
using Rosetta.Binary;
using Rosetta.Common;
using Rosetta.Lifter.IR.Nodes;
using Rosetta.Metadata;
using Rosetta.Model;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.IR;

// ══════════════════════════════════════════════════════════════════════════
// Core: Constructor, ResolveAll, Statistics
// Pass 1: Metadata Table Chain Resolution
// Pass 3/3.5: Immediate Decoding + Float Annotation
// Pass 5: Struct/Vector Annotation
// ══════════════════════════════════════════════════════════════════════════

public sealed partial class IrDataResolver
{
    private readonly GlobalAddressMap? _addressMap;
    private readonly MetadataParser? _metadata;
    private readonly RegistrationResolver? _registration;
    private readonly TypeResolver? _typeResolver;
    private readonly TypeModel? _typeModel;
    private readonly FieldAnnotation.FieldMetadataResolver _fieldMetadataResolver;

    // Statistics
    public int MetadataRefsResolved { get; private set; }
    public int RuntimeHelpersClassified { get; private set; }
    public int ImmediatesDecoded { get; private set; }
    public int FieldsAnnotated { get; private set; }
    public int VectorsAnnotated { get; private set; }

    internal FieldAnnotation.FieldMetadataResolver FieldMetadataResolver => _fieldMetadataResolver;
    internal int FindTypeDefIndex(string typeName) => _fieldMetadataResolver.FindTypeDefIndex(typeName);

    public IrDataResolver(
        GlobalAddressMap? addressMap = null,
        MetadataParser? metadata = null,
        RegistrationResolver? registration = null,
        TypeResolver? typeResolver = null,
        TypeModel? typeModel = null)
    {
        _addressMap = addressMap;
        _metadata = metadata;
        _registration = registration;
        _typeResolver = typeResolver;
        _typeModel = typeModel;
        _fieldMetadataResolver = new FieldAnnotation.FieldMetadataResolver(metadata, registration, typeResolver, typeModel);
    }

    /// <summary>
    /// Run all resolution passes on a method's IR instructions.
    /// Order matters: metadata refs first (provides type info), then helper classification
    /// (uses type info), then immediates (context-independent).
    /// </summary>
    public void ResolveAll(IrMethod method)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"IrDataResolver.ResolveAll: {method.MethodName} ({method.Instructions.Count} instrs)");
        var insts = method.Instructions;
        if (insts.Count == 0) return;

        // Build def-use index for backward tracing
        var duIndex = new DefUseIndex(insts);

        // Pass 1: Resolve metadata table address chains
        ResolveMetadataChains(insts, duIndex);

        // Pass 2: Classify runtime helper calls
        ClassifyRuntimeHelpers(insts, duIndex);

        // Pass 3: Decode hex immediates (float bit-patterns, char literals)
        DecodeImmediates(insts);

        // Pass 3.5: Annotate float immediates with clean formatted strings.
        // After DecodeImmediates converts int bit-patterns to FloatImmediate,
        // and IrLifter emits FMOV/MOVZ+MOVK as FloatImmediate sources,
        // set annotations so ExprPropagator's BuildLoadImm picks them up
        // as ExprVar (clean, no quotes, InvariantCulture).
        AnnotateFloatImmediates(insts);

        // Pass 4: Annotate field offsets — now with full field name resolution
        AnnotateFieldOffsets(insts, duIndex, method);

        // Pass 5: Struct/Vector annotation (HFA returns, rodata float constants, SIMD ops)
        AnnotateStructsAndVectors(insts);

        // Pass 6: Resolve vtable indirect calls to method names
        ResolveVTableCalls(insts);
        
        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  IrDataResolver: Done resolving dataflow semantics");
    }



    public string GetStatsSummary() =>
        $"Resolver: {MetadataRefsResolved} metadata, {RuntimeHelpersClassified} helpers, " +
        $"{ImmediatesDecoded} immediates, {FieldsAnnotated} fields, {VectorsAnnotated} vectors";

    // ══════════════════════════════════════════════════════════════════════════
    // Pass 1: Metadata Table Chain Resolution
    // ══════════════════════════════════════════════════════════════════════════
    // Pattern: x = addr 0x2414000 → x = load [x + 0xNNN] → y = load [x]
    // The addr + offset gives a metadata slot VA. GlobalAddressMap can decode that
    // slot into a string literal, type name, method name, etc.

    private void ResolveMetadataChains(List<IrInstruction> insts, DefUseIndex duIndex)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    IrDataResolver.ResolveMetadataChains");
        if (_addressMap == null) return;

        for (int i = 0; i < insts.Count; i++)
        {
            var inst = insts[i];
            if (inst.Opcode != IrOpcode.LoadAddress) continue;
            if (inst.Sources.Length == 0) continue;

            // Get the ADRP page address
            long pageAddr = inst.Sources[0].Value;

            // Find the subsequent load that adds the page-offset
            // Pattern: x = addr PAGE → x = load [x + OFFSET]
            var uses = duIndex.GetUses(i);
            foreach (int useIdx in uses)
            {
                var useInst = insts[useIdx];
                if (useInst.Opcode != IrOpcode.Load) continue;
                if (useInst.Sources.Length == 0) continue;
                if (useInst.Sources[0].Kind != IrOperandKind.Memory) continue;

                long offset = useInst.Sources[0].Offset;
                ulong targetVA = (ulong)(pageAddr + offset);

                // Try to resolve via GlobalAddressMap
                var annotation = _addressMap.ResolveAddress(targetVA);
                if (annotation != null && annotation.Kind != AddressKind.Unknown)
                {
                    // Annotate the load instruction with the resolved meaning
                    string label = annotation.Kind switch
                    {
                        AddressKind.StringLiteral => $"\"{annotation.Label}\"",
                        AddressKind.RuntimeClass => $"typeof({annotation.Label})",
                        AddressKind.RuntimeType => $"type({annotation.Label})",
                        AddressKind.MethodInfo => $"MethodInfo({annotation.Label})",
                        AddressKind.FieldInfo => $"field({annotation.Label})",
                        AddressKind.FieldRva => $"field({annotation.Label})",
                        AddressKind.MethodRef => $"MethodRef({annotation.Label})",
                        _ => annotation.Label,
                    };

                    useInst.SemanticTag = annotation.Kind switch
                    {
                        AddressKind.MethodRef => IrSemanticTag.MethodRef,
                        AddressKind.ClassInitFlag => IrSemanticTag.ClassInit,
                        _ => IrSemanticTag.None
                    };

                    if (annotation.Kind == AddressKind.MethodRef)
                    {
                        useInst.TargetMethodIndex = annotation.MetadataIndex;
                    }

                    useInst.MetadataKind = annotation.Kind;
                    useInst.MetadataIndex = annotation.MetadataIndex;

                    // Set annotation on the load instruction
                    useInst.Annotation = label;
                    MetadataRefsResolved++;

                    // Also annotate the addr instruction with the metadata page identity.
                    // A single page can contain mixed entries (strings + classes + methods),
                    // so we label by page address rather than content type.
                    inst.Annotation ??= $"il2cpp_metadata_page";
                }
                else
                {
                    if (ConsoleReporter.Verbose)
                    {
                        ConsoleReporter.Debug($"    → ResolveMetadataChains: Failed to resolve metadata VA 0x{targetVA:X} (page 0x{pageAddr:X} + offset 0x{offset:X})");
                    }
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Pass 3: Hex Immediate Decoding
    // ══════════════════════════════════════════════════════════════════════════
    // w registers with values that are float bit-patterns or printable chars

    private void DecodeImmediates(List<IrInstruction> insts)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    IrDataResolver.DecodeImmediates");
        for (int i = 0; i < insts.Count; i++)
        {
            var inst = insts[i];
            if (inst.Opcode != IrOpcode.LoadImmediate) continue;
            if (inst.Sources.Length == 0) continue;
            if (inst.Annotation != null) continue; // already annotated

            long val = inst.Sources[0].Value;
            if (val == 0) continue;

            var dst = inst.Destination;
            if (!dst.HasValue) continue;

            // Rule 1: W register with value that is a valid IEEE 754 float bit-pattern.
            // Covers both positive floats (sign bit 0) and negative floats (sign bit 1).
            // Convert source operand to FloatImmediate so ExprPropagator uses FormatFloat.
            if (dst.Value.BitWidth == 32)
            {
                uint uval = (uint)(val & 0xFFFFFFFF);
                // Exclude small integers (could be int constants, not float patterns)
                // and special values (0x7F800000 = +Inf, 0xFF800000 = -Inf)
                int exponent = (int)((uval >> 23) & 0xFF);
                if (exponent > 0 && exponent < 0xFF && uval > 0xFF)
                {
                    float f = BitConverter.Int32BitsToSingle((int)uval);
                    if (float.IsFinite(f) && MathF.Abs(f) >= 0.001f && MathF.Abs(f) < 1e10f)
                    {
                        if (IsLikelyFloatBitPattern(val & 0x7FFFFFFF, f))
                        {
                            // Convert to FloatImmediate — ExprPropagator handles the rest
                            inst.Sources[0] = IrOperand.FloatImmediate((long)uval, 32);
                            ImmediatesDecoded++;
                            continue;
                        }
                    }
                }
            }

            // Rule 2: [DELETED] Char guessing heuristic has been structurally replaced by AST-level type coercion.

            // Rule 3: Negative values via unsigned 32-bit representation.
            // Sign-extend and check if it's a small negative integer.
            if (dst.Value.BitWidth == 32)
            {
                int signed32 = (int)(uint)(val & 0xFFFFFFFF);
                if (signed32 < 0 && signed32 >= -1024)
                {
                    // Replace source with sign-extended value so FormatImm produces decimal
                    inst.Sources[0] = IrOperand.Immediate(signed32, 32);
                    ImmediatesDecoded++;
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Pass 3.5: Float Immediate Annotation
    // ══════════════════════════════════════════════════════════════════════════
    // For every LoadImmediate with a FloatImmediate source, set annotation to
    // clean InvariantCulture-formatted string so ExprPropagator's BuildLoadImm
    // picks it up via ExprVar (no quotes, correct decimal separator).

    private static void AnnotateFloatImmediates(List<IrInstruction> insts)
    {
        for (int i = 0; i < insts.Count; i++)
        {
            var inst = insts[i];
            if (inst.Opcode != IrOpcode.LoadImmediate) continue;
            if (inst.Sources.Length == 0) continue;
            if (inst.Annotation != null) continue;
            if (inst.Sources[0].Kind != IrOperandKind.FloatImmediate) continue;

            long rawBits = inst.Sources[0].Value;
            byte bitWidth = inst.Sources[0].BitWidth;

            if (bitWidth <= 32)
            {
                float f = BitConverter.Int32BitsToSingle((int)rawBits);
                inst.Annotation = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, "{0:G}f", f);
            }
            else
            {
                double d = BitConverter.Int64BitsToDouble(rawBits);
                inst.Annotation = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, "{0:G}d", d);
            }
        }
    }

    /// <summary>
    /// Check if a raw 32-bit value is likely an IEEE 754 float bit-pattern
    /// rather than a regular integer.
    /// Heuristic: "clean" floats that compile to MOV immediate (round numbers like 5.0, 100.0).
    /// </summary>
    private static bool IsLikelyFloatBitPattern(long rawBits, float value)
    {
        // Must be above small integer range (> 256) to avoid int/char confusion
        if (rawBits <= 0xFF) return false;

        // Float bit-patterns have exponent field in bits 23-30
        int exponent = (int)((rawBits >> 23) & 0xFF);
        // Valid float range: exponent 1-254 (exclude denormals and inf/nan)
        if (exponent == 0 || exponent == 0xFF) return false;

        // Check if the float value is a "clean" number (integer or simple fraction)
        float rounded = MathF.Round(value, 2);
        if (MathF.Abs(value - rounded) < 0.001f) return true;

        // Known IL2CPP float constants
        if (MathF.Abs(value - MathF.PI) < 0.0001f) return true;

        return false;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Pass 5: Struct/Vector Annotation
    // ══════════════════════════════════════════════════════════════════════════
    // Sub-pass A: HFA return splitting — after calls that return Vector3/Quaternion/etc.,
    //   the register captures (s8=s0, s9=s1, s10=s2) are annotated with component names.
    // Sub-pass B: Rodata packed constants — D-register loads from .rodata are decoded
    //   as two packed float32 values (Vector2/3 x,y initialization).
    // Sub-pass C: SIMD op context — annotates simd_op instructions with vector type info.

    private void AnnotateStructsAndVectors(List<IrInstruction> insts)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"    IrDataResolver.AnnotateStructsAndVectors");
        for (int i = 0; i < insts.Count; i++)
        {
            var inst = insts[i];

            // ── Sub-pass A: HFA Return Splitting ─────────────────────────
            if (inst.Opcode == IrOpcode.Call && inst.Annotation != null)
            {
                var sig = _typeModel?.GetMethodSignature(inst.Annotation);
                if (sig != null && sig.ReturnHfaSize > 0 && sig.ReturnHfaFieldNames != null)
                {
                    string hfaType = sig.ReturnTypeName;
                    int lastDot = hfaType.LastIndexOf('.');
                    if (lastDot >= 0) hfaType = hfaType[(lastDot + 1)..];

                    var components = sig.ReturnHfaFieldNames;

                    // Look forward for sN = s0/s1/s2/s3 captures
                    for (int j = i + 1; j < Math.Min(i + components.Length + 4, insts.Count); j++)
                    {
                        var next = insts[j];
                        if (next.Opcode is IrOpcode.Call or IrOpcode.Branch or IrOpcode.ConditionalBranch)
                            break;

                        // sN = sM  (Assign from FP register)
                        if (next.Opcode == IrOpcode.Assign && next.Annotation == null &&
                            next.Destination.HasValue &&
                            next.Sources.Length > 0 &&
                            next.Sources[0].Kind == IrOperandKind.FpRegister)
                        {
                            int srcReg = (int)next.Sources[0].Value;
                            bool isFpSrc = next.Sources[0].BitWidth == 32;
                            bool isFpDst = next.Destination.Value.Kind == IrOperandKind.FpRegister &&
                                           next.Destination.Value.BitWidth == 32;

                            if (isFpSrc && isFpDst && srcReg >= 0 && srcReg < components.Length)
                            {
                                next.Annotation = $"{hfaType}.{components[srcReg]}";
                                VectorsAnnotated++;
                            }
                        }
                    }
                }
            }

        }
    }



    /// <summary>Scan forward from a D-register load to find a box call that consumes it.</summary>
    private static string? FindBoxTypeForward(List<IrInstruction> insts, int idx)
    {
        for (int j = idx + 1; j < Math.Min(idx + 10, insts.Count); j++)
        {
            var fwd = insts[j];
            if (fwd.Opcode == IrOpcode.Call && fwd.Annotation != null && fwd.Annotation.StartsWith("box<"))
            {
                // Extract type from box<TypeName>
                int start = 4;
                int end = fwd.Annotation.IndexOf('>');
                if (end > start) return fwd.Annotation[start..end];
            }
        }
        return null;
    }

    /// <summary>
    /// Trace backward from a static_fields load to find which class owns it.
    /// Returns the TypeDefIndex of the owning class, or the method's own type as fallback.
    /// Pattern: x = load [classPtr] → classPtr was loaded from metadata → typeof(ClassName)
    /// </summary>

    /// <summary>Extract a clean type name from an annotation like "typeof(System.Int32)".</summary>
    internal static string ExtractTypeName(string annotation)
    {
        while (true)
        {
            int mIdx = annotation.IndexOf("[M:");
            if (mIdx >= 0)
            {
                int closeBracket = annotation.IndexOf(']', mIdx);
                if (closeBracket > mIdx)
                {
                    annotation = annotation.Remove(mIdx, closeBracket - mIdx + 1).Trim();
                    annotation = annotation.Replace("  ", " ");
                    continue;
                }
            }
            int hfaIdx = annotation.IndexOf("[HFA:");
            if (hfaIdx >= 0)
            {
                int closeBracket = annotation.IndexOf(']', hfaIdx);
                if (closeBracket > hfaIdx)
                {
                    annotation = annotation.Remove(hfaIdx, closeBracket - hfaIdx + 1).Trim();
                    annotation = annotation.Replace("  ", " ");
                    continue;
                }
            }
            break;
        }

        if (annotation.StartsWith("typeof(") && annotation.EndsWith(")"))
            return annotation[7..^1];
        if (annotation.StartsWith("type(") && annotation.EndsWith(")"))
            return annotation[5..^1];
        if (annotation.StartsWith("\"") && annotation.EndsWith("\""))
            return annotation[1..^1];
        if (annotation.StartsWith("new ") && annotation.EndsWith("()"))
            return annotation[4..^2];
        if (annotation.StartsWith("box<") && annotation.EndsWith(">"))
            return annotation[4..^1];
        if (annotation.StartsWith("new ") && annotation.Contains('['))
        {
            int idx = annotation.IndexOf('[');
            return annotation[4..idx] + "[]";
        }
        return annotation;
    }
}
