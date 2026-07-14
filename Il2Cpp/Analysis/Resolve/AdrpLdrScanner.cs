using Rosetta.Binary;
using Rosetta.Pipeline;

namespace Rosetta.Analysis.Resolve;

/// <summary>
/// Scans decoded ARM64 instructions for ADRP+LDR/ADD pairs
/// and computes the resolved virtual address for each pair.
///
/// Pattern:
///   ADRP Xn, #page       → Xn = PC_page + immHi*4096
///   LDR  Xm, [Xn, #off]  → loads 8-byte ptr from Xn + off
///   LDRB Wm, [Xn, #off]  → loads byte from Xn + off (init flag)
///   ADD  Xm, Xn, #off    → Xm = Xn + off (address computation)
/// </summary>
public static class AdrpLdrScanner
{
    /// <summary>A resolved ADRP-relative memory access.</summary>
    public readonly struct ResolvedAccess
    {
        public readonly int InstructionIndex;
        public readonly ulong TargetVA;
        public readonly bool IsByteLdrb;
        public readonly int DestRegister;
        /// <summary>0=GP, 2=S(float), 3=D(double). From LDR_SIMD_IMM Shift field.</summary>
        public readonly int SimdSize;

        public ResolvedAccess(int idx, ulong va, bool isLdrb, int destReg, int simdSize = 0)
        {
            InstructionIndex = idx;
            TargetVA = va;
            IsByteLdrb = isLdrb;
            DestRegister = destReg;
            SimdSize = simdSize;
        }
    }

    /// <summary>Scan instructions and return all ADRP-relative resolved accesses.</summary>
    public static List<ResolvedAccess> Scan(Arm64Instruction[] instructions)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"  AdrpLdrScanner.Scan: {instructions.Length} instructions");
        var results = new List<ResolvedAccess>();

        // Track ADRP page bases: reg index → page VA
        var pages = new ulong[31];
        var valid = new bool[31];

        for (int i = 0; i < instructions.Length; i++)
        {
            ref readonly var inst = ref instructions[i];

            switch (inst.Opcode)
            {
                case Arm64Opcode.ADRP:
                {
                    int rd = inst.Rd;
                    if (rd < 31)
                    {
                        pages[rd] = (ulong)inst.Immediate;
                        valid[rd] = true;
                    }
                    break;
                }

                case Arm64Opcode.LDR_IMM:
                case Arm64Opcode.LDRSW_IMM:
                case Arm64Opcode.LDRH_IMM:
                {
                    int baseReg = inst.Rn;
                    if (baseReg < 31 && valid[baseReg])
                    {
                        ulong target = pages[baseReg] + (ulong)inst.Immediate;
                        results.Add(new ResolvedAccess(i, target, false, inst.Rd));

                        // Dest reg no longer holds a page
                        if (inst.Rd < 31)
                            valid[inst.Rd] = false;
                    }
                    break;
                }

                case Arm64Opcode.LDR_SIMD_IMM:
                {
                    // LDR Sn/Dn, [Xn, #off] — float/double from .rodata literal pool
                    int baseReg = inst.Rn;
                    if (baseReg < 31 && valid[baseReg])
                    {
                        ulong target = pages[baseReg] + (ulong)inst.Immediate;
                        // Shift encodes precision: 2=S(float), 3=D(double)
                        results.Add(new ResolvedAccess(i, target, false, -1, inst.Shift));
                    }
                    break;
                }

                case Arm64Opcode.LDRB_IMM:
                {
                    int baseReg = inst.Rn;
                    if (baseReg < 31 && valid[baseReg])
                    {
                        ulong target = pages[baseReg] + (ulong)inst.Immediate;
                        results.Add(new ResolvedAccess(i, target, true, inst.Rd));

                        if (inst.Rd < 31)
                            valid[inst.Rd] = false;
                    }
                    break;
                }

                case Arm64Opcode.STR_IMM:
                case Arm64Opcode.STRB_IMM:
                case Arm64Opcode.STRH_IMM:
                {
                    int baseReg = inst.Rn;
                    if (baseReg < 31 && valid[baseReg])
                    {
                        ulong target = pages[baseReg] + (ulong)inst.Immediate;
                        bool isByte = inst.Opcode == Arm64Opcode.STRB_IMM;
                        results.Add(new ResolvedAccess(i, target, isByte, -1));
                    }
                    break;
                }

                case Arm64Opcode.ADD_IMM:
                {
                    int rn = inst.Rn;
                    if (rn < 31 && valid[rn])
                    {
                        int rd = inst.Rd;
                        if (rd < 31)
                        {
                            pages[rd] = pages[rn] + (ulong)inst.Immediate;
                            valid[rd] = true;
                        }
                    }
                    break;
                }

                // Any instruction that writes Rd invalidates ADRP tracking
                default:
                {
                    int rd = inst.Rd;
                    if (rd < 31 && inst.Opcode != Arm64Opcode.Unknown)
                    {
                        // Only invalidate for instructions that write to Rd
                        if (Rosetta.Analysis.Utils.ArmUtils.IsWriteToRd(inst.Opcode))
                            valid[rd] = false;
                    }
                    break;
                }
            }
        }

        if (ConsoleReporter.IsTracing) ConsoleReporter.Debug($"  AdrpLdrScanner: {results.Count} resolved accesses");
        return results;
    }
}
