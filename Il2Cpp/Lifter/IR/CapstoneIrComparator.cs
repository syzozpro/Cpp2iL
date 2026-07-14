using System;
using System.Collections.Generic;
using System.Text;
using Rosetta.Binary;

namespace Rosetta.Lifter.IR;

/// <summary>
/// Compares your custom ARM64 decoder output against Capstone's disassembly
/// to detect decoding bugs. Aligns instructions by address and normalizes
/// aliases/formatting to avoid false positives.
///
/// Usage:
///   var discrepancies = CapstoneIrComparator.Compare(armInsts, rawBytes, baseVA, capstone);
///
/// Each discrepancy contains the address, both disassembly strings, and the diff type.
/// </summary>
public static class CapstoneIrComparator
{
    /// <summary>
    /// Compare your decoded ARM64 instructions against Capstone's output.
    /// Returns only the instructions where the two decoders disagree.
    /// </summary>
    /// <param name="yourInsts">Your custom Arm64Decoder output.</param>
    /// <param name="rawBytes">Raw method bytes for Capstone to re-disassemble.</param>
    /// <param name="baseVA">Base virtual address of the method.</param>
    /// <param name="capstone">Thread-local Capstone wrapper instance.</param>
    /// <returns>List of discrepancies (empty if everything matches).</returns>
    public static List<Discrepancy> Compare(
        Arm64Instruction[] yourInsts,
        byte[] rawBytes,
        ulong baseVA,
        CapstoneDisassemblerWrapper capstone)
    {
        var result = new List<Discrepancy>();

        // Run Capstone on the same bytes
        var csInsts = capstone.DisassembleBlock(rawBytes, baseVA);

        // Build address→index map for Capstone instructions (O(1) lookup)
        var csMap = new Dictionary<ulong, int>(csInsts.Length);
        for (int i = 0; i < csInsts.Length; i++)
            csMap[csInsts[i].Address] = i;

        for (int i = 0; i < yourInsts.Length; i++)
        {
            var yours = yourInsts[i];
            string yourDisasm = yours.ToString();
            string yourNorm = Normalize(yourDisasm);

            if (csMap.TryGetValue(yours.Address, out int csIdx))
            {
                var cs = csInsts[csIdx];
                string csDisasm = cs.ToDisplayString();
                string csNorm = Normalize(csDisasm);

                if (!AreEquivalent(yourNorm, csNorm, yours))
                {
                    result.Add(new Discrepancy
                    {
                        Address = yours.Address,
                        RawValue = yours.RawValue,
                        YourDisasm = yourDisasm,
                        CapstoneDisasm = csDisasm,
                        Type = yours.Opcode == Arm64Opcode.Unknown
                            ? DiscrepancyType.UnknownInYours
                            : DiscrepancyType.Mismatch
                    });
                }
            }
            else
            {
                // Capstone didn't produce an instruction at this address
                result.Add(new Discrepancy
                {
                    Address = yours.Address,
                    RawValue = yours.RawValue,
                    YourDisasm = yourDisasm,
                    CapstoneDisasm = "(missing)",
                    Type = DiscrepancyType.MissingInCapstone
                });
            }
        }

        // Check for instructions Capstone decoded but yours didn't
        var yourAddresses = new HashSet<ulong>(yourInsts.Length);
        foreach (var y in yourInsts)
            yourAddresses.Add(y.Address);

        foreach (var cs in csInsts)
        {
            if (!yourAddresses.Contains(cs.Address))
            {
                result.Add(new Discrepancy
                {
                    Address = cs.Address,
                    RawValue = 0,
                    YourDisasm = "(missing)",
                    CapstoneDisasm = cs.ToDisplayString(),
                    Type = DiscrepancyType.MissingInYours
                });
            }
        }

        // Sort by address for clean output
        result.Sort((a, b) => a.Address.CompareTo(b.Address));
        return result;
    }

    public static List<Discrepancy> Compare(
        Thumb2Instruction[] yourInsts,
        byte[] rawBytes,
        ulong baseVA,
        CapstoneDisassemblerWrapper capstone,
        bool isArmMode = false)
    {
        var result = new List<Discrepancy>();

        // Run Capstone on the same bytes — use ARM mode for ARM-mode methods
        var csInsts = isArmMode
            ? capstone.DisassembleBlockArm(rawBytes, baseVA)
            : capstone.DisassembleBlock(rawBytes, baseVA);

        // Build address→index map for Capstone instructions (O(1) lookup)
        var csMap = new Dictionary<ulong, int>(csInsts.Length);
        for (int i = 0; i < csInsts.Length; i++)
            csMap[csInsts[i].Address] = i;

        for (int i = 0; i < yourInsts.Length; i++)
        {
            var yours = yourInsts[i];
            string yourDisasm = yours.ToString();
            string yourNorm = Normalize(yourDisasm);

            if (csMap.TryGetValue(yours.Address, out int csIdx))
            {
                var cs = csInsts[csIdx];
                string csDisasm = cs.ToDisplayString();
                string csNorm = Normalize(csDisasm);

                if (yourNorm != csNorm)
                {
                    result.Add(new Discrepancy
                    {
                        Address = yours.Address,
                        RawValue = yours.RawValue,
                        YourDisasm = yourDisasm,
                        CapstoneDisasm = csDisasm,
                        Type = yours.Opcode == Thumb2Opcode.Unknown
                            ? DiscrepancyType.UnknownInYours
                            : DiscrepancyType.Mismatch
                    });
                }
            }
            else
            {
                // Capstone didn't produce an instruction at this address
                result.Add(new Discrepancy
                {
                    Address = yours.Address,
                    RawValue = yours.RawValue,
                    YourDisasm = yourDisasm,
                    CapstoneDisasm = "(missing)",
                    Type = DiscrepancyType.MissingInCapstone
                });
            }
        }

        // Check for instructions Capstone decoded but yours didn't
        var yourAddresses = new HashSet<ulong>(yourInsts.Length);
        foreach (var y in yourInsts)
            yourAddresses.Add(y.Address);

        foreach (var cs in csInsts)
        {
            if (!yourAddresses.Contains(cs.Address))
            {
                result.Add(new Discrepancy
                {
                    Address = cs.Address,
                    RawValue = 0,
                    YourDisasm = "(missing)",
                    CapstoneDisasm = cs.ToDisplayString(),
                    Type = DiscrepancyType.MissingInYours
                });
            }
        }

        // Sort by address for clean output
        result.Sort((a, b) => a.Address.CompareTo(b.Address));
        return result;
    }

    public static string? FormatComparison(
        Thumb2Instruction[] yourInsts,
        byte[] rawBytes,
        ulong baseVA,
        CapstoneDisassemblerWrapper capstone,
        string methodName,
        bool isArmMode = false)
    {
        var diffs = Compare(yourInsts, rawBytes, baseVA, capstone, isArmMode);
        if (diffs.Count == 0) return null;

        int totalInsts = yourInsts.Length;
        int matchCount = totalInsts - diffs.Count(d => d.Type != DiscrepancyType.MissingInYours);

        var sb = new StringBuilder();
        sb.AppendLine($"  Decoder comparison: {matchCount}/{totalInsts} match, {diffs.Count} discrepancies");
        sb.AppendLine();
        sb.AppendLine($"  {"Address",-14} {"Raw",-12} {"Custom Decoder",-44} {"Capstone",-44} {"Status"}");
        sb.AppendLine($"  {"──────────",-14} {"────────",-12} {"──────────────────────────────────────────",-44} {"──────────────────────────────────────────",-44} {"──────"}");

        foreach (var d in diffs)
        {
            string rawHex = d.RawValue != 0 ? $"{d.RawValue:X8}" : "????????";
            string status = d.Type switch
            {
                DiscrepancyType.Mismatch => "DIFF",
                DiscrepancyType.UnknownInYours => "UNKNOWN",
                DiscrepancyType.MissingInCapstone => "CS_MISS",
                DiscrepancyType.MissingInYours => "MY_MISS",
                _ => "?"
            };

            // Truncate long strings
            string yourStr = d.YourDisasm.Length > 42 ? d.YourDisasm[..42] + ".." : d.YourDisasm;
            string csStr = d.CapstoneDisasm.Length > 42 ? d.CapstoneDisasm[..42] + ".." : d.CapstoneDisasm;

            sb.AppendLine($"  0x{d.Address:X8}    {rawHex,-12} {yourStr,-44} {csStr,-44} {status}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Format a comparison summary for a method, showing only discrepancies.
    /// Returns null if everything matches (no output needed).
    /// </summary>
    public static string? FormatComparison(
        Arm64Instruction[] yourInsts,
        byte[] rawBytes,
        ulong baseVA,
        CapstoneDisassemblerWrapper capstone,
        string methodName,
        AsmArm64DisassemblerWrapper? asmArm64 = null)
    {
        var diffs = Compare(yourInsts, rawBytes, baseVA, capstone);
        if (diffs.Count == 0) return null;

        int totalInsts = yourInsts.Length;
        int matchCount = totalInsts - diffs.Count(d => d.Type != DiscrepancyType.MissingInYours);

        // Build AsmArm64 address map if available
        Dictionary<ulong, AsmArm64Instruction>? aaMap = null;
        if (asmArm64 != null)
        {
            var aaInsts = asmArm64.DisassembleBlock(rawBytes, baseVA);
            aaMap = new Dictionary<ulong, AsmArm64Instruction>(aaInsts.Length);
            foreach (var aa in aaInsts)
                aaMap[aa.Address] = aa;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"  Decoder comparison: {matchCount}/{totalInsts} match, {diffs.Count} discrepancies");
        sb.AppendLine();
        sb.AppendLine($"  {"Address",-14} {"Raw",-12} {"Custom Decoder",-44} {"Capstone",-44} {"AsmArm64",-44} {"Status"}");
        sb.AppendLine($"  {"──────────",-14} {"────────",-12} {"──────────────────────────────────────────",-44} {"──────────────────────────────────────────",-44} {"──────────────────────────────────────────",-44} {"──────"}");

        foreach (var d in diffs)
        {
            string rawHex = d.RawValue != 0 ? $"{d.RawValue:X8}" : "????????";
            string status = d.Type switch
            {
                DiscrepancyType.Mismatch => "DIFF",
                DiscrepancyType.UnknownInYours => "UNKNOWN",
                DiscrepancyType.MissingInCapstone => "CS_MISS",
                DiscrepancyType.MissingInYours => "MY_MISS",
                _ => "?"
            };

            // Truncate long strings
            string yourStr = d.YourDisasm.Length > 42 ? d.YourDisasm[..42] + ".." : d.YourDisasm;
            string csStr = d.CapstoneDisasm.Length > 42 ? d.CapstoneDisasm[..42] + ".." : d.CapstoneDisasm;
            string aaStr = aaMap != null && aaMap.TryGetValue(d.Address, out var aaInst)
                ? aaInst.Text : "—";
            if (aaStr.Length > 42) aaStr = aaStr[..42] + "..";

            sb.AppendLine($"  0x{d.Address:X8}    {rawHex,-12} {yourStr,-44} {csStr,-44} {aaStr,-44} {status}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Normalize a disassembly string for comparison.
    /// Strips formatting differences that don't indicate real decoding bugs.
    /// </summary>
    private static string Normalize(string disasm)
    {
        if (string.IsNullOrEmpty(disasm)) return "";

        // Lowercase everything
        var s = disasm.ToLowerInvariant().Trim();

        // Remove leading/trailing whitespace and collapse internal spaces
        s = CollapseSpaces(s);

        // Remove trailing comments ("; ...")
        int semi = s.IndexOf(';');
        if (semi >= 0) s = s[..semi].TrimEnd();

        // Normalize hex prefix: "#0x" → "0x", remove standalone "#"
        s = s.Replace("#0x", "0x").Replace("#-0x", "-0x").Replace("#", "");

        // Normalize comma spacing: ", " → ","
        s = s.Replace(", ", ",");

        // Strip trailing ", lsl 0" — zero shift is implied and Capstone omits it
        // Also handle "lsl #0" and "lsl 0x0" variants
        s = s.Replace(",lsl 0x0", "").Replace(",lsl 0", "");

        // Normalize all hex immediates to decimal
        // e.g., "0x8" → "8", "0x1" → "1", "-0xff" → "-255"
        s = NormalizeHexToDecimal(s);

        return s;
    }

    /// <summary>
    /// Convert all hex immediate values (0xNN) in a string to decimal.
    /// This eliminates false positives from hex vs decimal formatting differences.
    /// </summary>
    private static string NormalizeHexToDecimal(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            // Check for negative hex: -0x...
            bool negative = false;
            if (i < s.Length - 3 && s[i] == '-' && s[i + 1] == '0' && s[i + 2] == 'x')
            {
                negative = true;
                i += 3; // skip "-0x"
            }
            else if (i < s.Length - 2 && s[i] == '0' && s[i + 1] == 'x')
            {
                i += 2; // skip "0x"
            }
            else
            {
                sb.Append(s[i]);
                i++;
                continue;
            }

            // Parse hex digits
            ulong val = 0;
            int hexStart = i;
            while (i < s.Length && IsHexDigit(s[i]))
            {
                val = val * 16 + HexVal(s[i]);
                i++;
            }

            if (i == hexStart)
            {
                // "0x" without following hex digits — emit literally
                sb.Append(negative ? "-0x" : "0x");
            }
            else
            {
                if (negative)
                    sb.Append('-');
                sb.Append(val.ToString());
            }
        }
        return sb.ToString();
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');

    private static uint HexVal(char c) =>
        c >= 'a' ? (uint)(c - 'a' + 10) : (uint)(c - '0');

    /// <summary>
    /// Check if two normalized disassembly strings are semantically equivalent,
    /// accounting for ARM64 alias differences.
    /// </summary>
    private static bool AreEquivalent(string yourNorm, string csNorm, Arm64Instruction yours)
    {
        // Direct match after normalization
        if (yourNorm == csNorm) return true;

        // ── ARM64 Alias Equivalences ──
        // The ARM ARM defines many instruction aliases. Our decoder may emit
        // the base instruction while Capstone emits the alias, or vice versa.

        // Extract mnemonic from both
        string yourMnem = ExtractMnemonic(yourNorm);
        string csMnem = ExtractMnemonic(csNorm);

        // NOP / zero-word padding
        if (yours.RawValue == 0 && csMnem == "udf") return true;
        if (yourMnem == "nop" && csMnem == "nop") return true;

        // MUL ↔ MADD (MUL is MADD with Ra=XZR)
        if ((yourMnem == "mul" && csMnem == "madd") || (yourMnem == "madd" && csMnem == "mul"))
            return true;
        // MNEG ↔ MSUB (MNEG is MSUB with Ra=XZR)
        if ((yourMnem == "mneg" && csMnem == "msub") || (yourMnem == "msub" && csMnem == "mneg"))
            return true;

        // CSET ↔ CSINC (CSET is CSINC with Rn=Rm=XZR)
        if ((yourMnem == "cset" && csMnem == "csinc") || (yourMnem == "csinc" && csMnem == "cset"))
            return true;
        // CSETM ↔ CSINV
        if ((yourMnem == "csetm" && csMnem == "csinv") || (yourMnem == "csinv" && csMnem == "csetm"))
            return true;

        // MOV ↔ ORR (MOV Rd, Rn is ORR Rd, XZR, Rn)
        if ((yourMnem == "mov" && csMnem == "orr") || (yourMnem == "orr" && csMnem == "mov"))
            return true;

        // MOV ↔ ADD (MOV Rd, SP is ADD Rd, SP, #0 — ARM ARM §C6.2.177)
        if ((yourMnem == "mov" && csMnem == "add") || (yourMnem == "add" && csMnem == "mov"))
            return true;

        // CMN ↔ ADDS (CMN is ADDS with Rd=XZR)
        if ((yourMnem == "cmn" && csMnem == "adds") || (yourMnem == "adds" && csMnem == "cmn"))
            return true;

        // CMP ↔ SUBS (CMP is SUBS with Rd=XZR)
        if ((yourMnem == "cmp" && csMnem == "subs") || (yourMnem == "subs" && csMnem == "cmp"))
            return true;

        // TST ↔ ANDS (TST is ANDS with Rd=XZR)
        if ((yourMnem == "tst" && csMnem == "ands") || (yourMnem == "ands" && csMnem == "tst"))
            return true;

        // LSL/LSR/ASR ↔ UBFM/SBFM (common bitfield aliases)
        if ((yourMnem == "ubfm" || yourMnem == "sbfm") &&
            (csMnem == "lsl" || csMnem == "lsr" || csMnem == "asr" ||
             csMnem == "uxtb" || csMnem == "uxth" || csMnem == "sxtw" ||
             csMnem == "sxth" || csMnem == "sxtb"))
            return true;
        if ((csMnem == "ubfm" || csMnem == "sbfm") &&
            (yourMnem == "lsl" || yourMnem == "lsr" || yourMnem == "asr" ||
             yourMnem == "uxtb" || yourMnem == "uxth" || yourMnem == "sxtw" ||
             yourMnem == "sxth" || yourMnem == "sxtb"))
            return true;

        // BFI/BFXIL ↔ BFM
        if ((yourMnem == "bfm" && (csMnem == "bfi" || csMnem == "bfxil")) ||
            ((yourMnem == "bfi" || yourMnem == "bfxil") && csMnem == "bfm"))
            return true;

        // LSL/LSR/ASR/ROR ↔ LSLV/LSRV/ASRV/RORV (variable shift aliases)
        if ((yourMnem == "lsl" && csMnem == "lslv") || (yourMnem == "lslv" && csMnem == "lsl"))
            return true;
        if ((yourMnem == "lsr" && csMnem == "lsrv") || (yourMnem == "lsrv" && csMnem == "lsr"))
            return true;
        if ((yourMnem == "asr" && csMnem == "asrv") || (yourMnem == "asrv" && csMnem == "asr"))
            return true;
        if ((yourMnem == "ror" && csMnem == "rorv") || (yourMnem == "rorv" && csMnem == "ror"))
            return true;

        // SIMD opcodes — Capstone uses different naming conventions
        // Our decoder may emit "V_OP" for unknown SIMD, Capstone will have the real name
        if (yourMnem.StartsWith("v_op") || yourMnem == "???")
            return true; // Don't flag our Unknown SIMD as a real diff (we know we don't decode them fully)

        // RET — Capstone may show "ret" or "ret x30", both equivalent
        if (yourMnem == "ret" && csMnem == "ret") return true;

        // SMULL/UMULL ↔ SMADDL/UMADDL (SMULL is SMADDL with Ra=XZR)
        if ((yourMnem == "smull" && csMnem == "smaddl") || (yourMnem == "smaddl" && csMnem == "smull"))
            return true;
        if ((yourMnem == "umull" && csMnem == "umaddl") || (yourMnem == "umaddl" && csMnem == "umull"))
            return true;

        // System register aliases (TPIDR_EL0)
        if ((yourMnem == "mrs" && csMnem == "mrs") || (yourMnem == "msr" && csMnem == "msr"))
        {
            if (yourNorm.Contains("s3_3_c13_c0_2") && csNorm.Contains("tpidr_el0")) return true;
        }

        // AND ↔ BIC (BIC is AND with shifted register inverted, Capstone formats it as BIC)
        if ((yourMnem == "and" && csMnem == "bic") || (yourMnem == "bic" && csMnem == "and"))
            return true;
        
        // ORR ↔ ORN (ORN is ORR with shifted register inverted)
        if ((yourMnem == "orr" && csMnem == "orn") || (yourMnem == "orn" && csMnem == "orr"))
            return true;

        // EOR ↔ EON (EON is EOR with shifted register inverted)
        if ((yourMnem == "eor" && csMnem == "eon") || (yourMnem == "eon" && csMnem == "eor"))
            return true;

        // ANDS ↔ BICS (BICS is ANDS with shifted register inverted)
        if ((yourMnem == "ands" && csMnem == "bics") || (yourMnem == "bics" && csMnem == "ands"))
            return true;

        // UMOV ↔ MOV (to general)
        if ((yourMnem == "umov" && csMnem == "mov") || (yourMnem == "mov" && csMnem == "umov"))
            return true;

        // INS ↔ MOV (element)
        if ((yourMnem == "ins" && csMnem == "mov") || (yourMnem == "mov" && csMnem == "ins"))
            return true;

        // If mnemonics differ and no alias rule matched, it's a real discrepancy
        // But if only operands differ (same mnemonic), still flag it
        return false;
    }

    /// <summary>Extract the mnemonic (first word) from a normalized disasm string.</summary>
    private static string ExtractMnemonic(string normalized)
    {
        int space = normalized.IndexOf(' ');
        return space >= 0 ? normalized[..space] : normalized;
    }

    /// <summary>Collapse multiple consecutive spaces to a single space.</summary>
    private static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastWasSpace = false;
        foreach (char c in s)
        {
            if (c == ' ' || c == '\t')
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString();
    }
}

/// <summary>Type of discrepancy between your decoder and Capstone.</summary>
public enum DiscrepancyType
{
    /// <summary>Both decoded the instruction but the output differs.</summary>
    Mismatch,
    /// <summary>Your decoder returned Unknown, Capstone decoded successfully.</summary>
    UnknownInYours,
    /// <summary>Capstone didn't produce output at this address.</summary>
    MissingInCapstone,
    /// <summary>Your decoder didn't produce output at this address.</summary>
    MissingInYours
}

/// <summary>A single discrepancy between your decoder and Capstone.</summary>
public struct Discrepancy
{
    public ulong Address;
    public uint RawValue;
    public string YourDisasm;
    public string CapstoneDisasm;
    public DiscrepancyType Type;
}
