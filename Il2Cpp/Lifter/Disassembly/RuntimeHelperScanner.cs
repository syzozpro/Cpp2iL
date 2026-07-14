using System;
using System.Collections.Generic;
using System.Linq;
using Rosetta.Binary;
using Rosetta.Lifter.ClangRules;
using Rosetta.Pipeline;

namespace Rosetta.Lifter.Disassembly;

/// <summary>
/// Scans the binary and ELF symbols to identify and register IL2CPP runtime helpers.
///
/// Resolution strategy (universal, version-independent):
///   1. ELF .dynsym — register ALL exported symbols (covers every C/C++ runtime function)
///   2. RuntimeFunctionProber / Thumb2RuntimeFunctionProber — identify inline stubs by instruction patterns
///   3. Per-module pointer arrays — ProbeUnresolvedUserMethods for cross-assembly methods
///   4. Remaining unknowns — label as sub_XXXXXX (standard reverse-engineering convention)
///
/// Supports both ARM64 (AArch64) and ARM32 (armeabi-v7a / Thumb2) architectures.
/// No hardcoded addresses, no proximity-based guessing, no version-specific logic.
/// </summary>
public sealed class RuntimeHelperScanner
{
    private readonly ReadOnlyMemory<byte> _binaryData;
    private readonly IBinaryParser _elf;
    private readonly CallResolver _callResolver;
    private readonly RuntimeFunctionProber? _prober64;
    private readonly Thumb2RuntimeFunctionProber? _prober32;
    private readonly bool _isArm32;
    private bool _preScanned;

    public RuntimeHelperScanner(ReadOnlyMemory<byte> binaryData, IBinaryParser elf, CallResolver callResolver)
    {
        _binaryData = binaryData;
        _elf = elf;
        _callResolver = callResolver;
        _isArm32 = elf.Is32Bit;
        if (_isArm32)
            _prober32 = new Thumb2RuntimeFunctionProber(binaryData, elf);
        else
            _prober64 = new RuntimeFunctionProber(binaryData, elf);
    }

    /// <summary>
    /// Register ALL ELF dynamic symbols with non-zero addresses.
    ///
    /// This is the universal approach: any function exported in the ELF .dynsym
    /// table has an authoritative name. This covers:
    ///   - il2cpp_codegen_* runtime helpers
    ///   - __cxa_* C++ exception handling functions
    ///   - Standard C library functions
    ///   - Any other exported symbol
    ///
    /// No prefix filtering — every symbol is registered.
    /// </summary>
    public void AutoRegisterRuntimeHelpers()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"RuntimeHelperScanner.AutoRegisterRuntimeHelpers()");
        int registered = 0;

        // Phase 1: Register all .dynsym symbols with non-zero addresses
        // (covers every exported/defined function in the binary)
        foreach (var sym in _elf.DynSymbols)
        {
            if (sym.Value == 0 || sym.ResolvedName == null) continue;

            // ELF symbol type 2 = STT_FUNC (function), but we also accept
            // type 0 (STT_NOTYPE) since some linkers don't set it.
            // Only skip type 1 (STT_OBJECT) which is data.
            if (sym.Type == 1) continue; // STT_OBJECT = data, not code

            // ARM32: strip Thumb bit (bit 0) from function addresses.
            // BL target addresses don't include this bit, so we must normalize.
            ulong symAddr = sym.Value;
            if (_isArm32) symAddr &= ~1UL;

            _callResolver.RegisterRuntimeFunction(symAddr, sym.ResolvedName);
            registered++;
        }

        // Phase 2: Register PLT stub entry points
        // BL instructions target PLT entry VAs for imported functions.
        // These VAs are NOT in .dynsym (imports have Value=0 there).
        // The IBinaryParser decodes each PLT stub to derive the GOT slot → symbol name.
        int pltRegistered = 0;
        foreach (var (pltVA, symName) in _elf.PltStubSymbols)
        {
            if (_callResolver.TryResolve(pltVA) == null)
            {
                _callResolver.RegisterRuntimeFunction(pltVA, symName);
                pltRegistered++;
            }
        }
        registered += pltRegistered;

        if (registered > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Auto-registered {registered} ELF symbols ({pltRegistered} PLT stubs)");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Pre-scan all method bodies to collect unresolved BL targets and identify
    /// internal IL2CPP runtime helpers by their instruction patterns.
    ///
    /// Scanning strategy:
    ///   - ARM64: Scan for fixed-width 4-byte BL instructions
    ///   - ARM32: Scan for variable-width Thumb2 BL/BLX instructions with veneer following
    ///   - Do NOT stop at RET — exception handlers (catch/finally) are placed
    ///     after the method's RET instruction in the same compilation unit
    /// </summary>
    public void PreScanAllMethods(Dictionary<int, ulong> methodAddressMap, HashSet<ulong>? thumbMethodAddresses = null)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"RuntimeHelperScanner.PreScanAllMethods: {methodAddressMap.Count} methods");
        if (_preScanned) return;
        _preScanned = true;

        // Build sorted list of all method start addresses for boundary detection
        var sortedMethodVAs = methodAddressMap.Values
            .Where(v => v != 0)
            .Distinct()
            .OrderBy(v => v)
            .ToArray();

        var globalCallCounts = new Dictionary<ulong, int>();
        var methodVAs = methodAddressMap.Values.Where(v => v != 0).ToList();
        object mergeLock = new object();

        System.Threading.Tasks.Parallel.ForEach(
            methodVAs,
            () => new Dictionary<ulong, int>(),
            (va, state, localCounts) =>
            {
                long fileOffset = _elf.VirtualToFileOffset(va);
                if (fileOffset < 0 || fileOffset + 16 > _binaryData.Length) return localCounts;

                int searchIdx = Array.BinarySearch(sortedMethodVAs, va);
                int insertionPoint = searchIdx >= 0 ? searchIdx : ~searchIdx;
                ulong nextMethodVA = (insertionPoint + 1 < sortedMethodVAs.Length)
                    ? sortedMethodVAs[insertionPoint + 1]
                    : va + 8192;

                int methodSize = (int)Math.Min(nextMethodVA - va, 8192);
                int maxBytes = Math.Min(_binaryData.Length - (int)fileOffset, methodSize);
                if (maxBytes < 4) return localCounts;

                var span = _binaryData.Span.Slice((int)fileOffset, maxBytes);

                if (_isArm32)
                {
                    // ARM32 binaries contain both Thumb and ARM mode methods.
                    // Must use the correct BL decoder for each mode.
                    bool isThumb = thumbMethodAddresses != null && thumbMethodAddresses.Contains(va);
                    if (isThumb)
                        ScanThumb2BLTargets(span, (uint)va, localCounts);
                    else
                        ScanArm32BLTargets(span, (uint)va, localCounts);
                }
                else
                {
                    ScanArm64BLTargets(span, va, localCounts);
                }
                return localCounts;
            },
            localCounts =>
            {
                lock (mergeLock)
                {
                    foreach (var kvp in localCounts)
                    {
                        globalCallCounts.TryGetValue(kvp.Key, out int count);
                        globalCallCounts[kvp.Key] = count + kvp.Value;
                    }
                }
            }
        );

        _unresolvedCallCounts = globalCallCounts;

        // Use the architecture-appropriate prober to identify inline runtime stubs
        Dictionary<ulong, string> identified;
        if (_isArm32)
            identified = _prober32!.ProbeHighFrequencyTargets(globalCallCounts, minFrequency: 3);
        else
            identified = _prober64!.ProbeHighFrequencyTargets(globalCallCounts, minFrequency: 3);

        foreach (var (addr, name) in identified)
        {
            _callResolver.RegisterRuntimeFunction(addr, name);
        }

        if (identified.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Pre-scan: identified {identified.Count} internal runtime helpers");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Scan ARM64 code for BL instructions (fixed 4-byte width).
    /// </summary>
    private void ScanArm64BLTargets(ReadOnlySpan<byte> span, ulong va, Dictionary<ulong, int> callCounts)
    {
        for (int i = 0; i < span.Length - 4; i += 4)
        {
            uint raw = (uint)(span[i] | (span[i + 1] << 8) | (span[i + 2] << 16) | (span[i + 3] << 24));

            if ((raw >> 26) == 0b100101) // BL
            {
                int imm26 = (int)(raw & 0x03FFFFFF);
                if ((imm26 & 0x02000000) != 0) imm26 |= unchecked((int)0xFC000000);
                ulong target = va + (ulong)i + (ulong)(imm26 << 2);

                if (_callResolver.TryResolve(target) == null)
                {
                    callCounts.TryGetValue(target, out int count);
                    callCounts[target] = count + 1;
                }
            }
        }
    }

    /// <summary>
    /// Scan Thumb2 code for BL/BLX instructions (variable 2/4 byte width),
    /// following veneers to resolve final targets.
    /// </summary>
    private void ScanThumb2BLTargets(ReadOnlySpan<byte> span, uint va, Dictionary<ulong, int> callCounts)
    {
        int i = 0;
        while (i < span.Length - 2)
        {
            if (Thumb2Decoder.Is32BitInstruction(span, i))
            {
                if (i + 4 > span.Length) break;

                if (Thumb2Decoder.IsBLorBLX(span, i))
                {
                    uint instrVA = va + (uint)i;
                    uint rawTarget = Thumb2Decoder.DecodeBLTarget(span, i, instrVA);

                    if (rawTarget != 0)
                    {
                        // Follow veneers to resolve the final target
                        uint resolved = Thumb2Decoder.FollowVeneer(
                            _binaryData.Span, _elf, rawTarget);
                        ulong target = resolved;

                        if (_callResolver.TryResolve(target) == null)
                        {
                            callCounts.TryGetValue(target, out int count);
                            callCounts[target] = count + 1;
                        }
                    }
                }
                i += 4;
            }
            else
            {
                i += 2;
            }
        }
    }

    /// <summary>
    /// Scan ARM32 ARM-mode code for BL/BLX instructions (fixed 4-byte width).
    /// ARM-mode BL encoding: cond[31:28] != 0xF, bits [27:25] = 0b101, bit [24] = 1 (link).
    /// ARM-mode BLX encoding: cond[31:28] = 0xF (unconditional), bits [27:25] = 0b101.
    /// </summary>
    private void ScanArm32BLTargets(ReadOnlySpan<byte> span, uint va, Dictionary<ulong, int> callCounts)
    {
        for (int i = 0; i <= span.Length - 4; i += 4)
        {
            uint raw = (uint)(span[i] | (span[i + 1] << 8) | (span[i + 2] << 16) | (span[i + 3] << 24));

            // ARM-mode BL: cond[31:28] != 0xF, bits [27:25] = 0b101, bit [24] = 1
            // ARM-mode BLX: cond[31:28] = 0xF, bits [27:25] = 0b101
            uint cond = (raw >> 28) & 0xF;
            uint bits27_25 = (raw >> 25) & 0x7;
            bool isBL = (bits27_25 == 0b101) && ((raw >> 24) & 1) == 1 && cond != 0xF;
            bool isBLX = (bits27_25 == 0b101) && cond == 0xF;

            if (!isBL && !isBLX) continue;

            // Decode signed 24-bit immediate
            int imm24 = (int)(raw & 0x00FFFFFF);
            if ((imm24 & 0x800000) != 0)
                imm24 |= unchecked((int)0xFF000000);

            uint instrVA = va + (uint)i;
            uint target;
            if (isBLX)
            {
                // BLX: H bit = bit[24], target = PC + (imm24 << 2) + (H << 1), aligned to 4
                int hBit = (int)((raw >> 24) & 1);
                target = (uint)((long)instrVA + 8 + ((long)imm24 << 2) + (hBit << 1));
                target &= ~3u;
            }
            else
            {
                // BL: target = PC + (imm24 << 2), where PC = instrVA + 8
                target = (uint)((long)instrVA + 8 + ((long)imm24 << 2));
            }

            if (target == 0) continue;

            // Follow veneers — veneers are always Thumb2 stubs even when called from ARM mode
            uint resolved = Thumb2Decoder.FollowVeneer(_binaryData.Span, _elf, target);
            ulong finalTarget = resolved;

            if (_callResolver.TryResolve(finalTarget) == null)
            {
                callCounts.TryGetValue(finalTarget, out int count);
                callCounts[finalTarget] = count + 1;
            }
        }
    }

    /// <summary>
    /// Saved unresolved call counts from PreScanAllMethods.
    /// </summary>
    private Dictionary<ulong, int>? _unresolvedCallCounts;

    /// <summary>
    /// Second-pass resolution: for BL targets still unresolved after runtime helper probing,
    /// search the raw per-module method pointer arrays to find matching VAs.
    ///
    /// This is a universal mechanism — it scans ALL per-module pointer tables
    /// and matches any unresolved VA against them. No hardcoded addresses.
    ///
    /// Returns the number of newly resolved methods.
    /// </summary>
    public int ProbeUnresolvedUserMethods(
        IReadOnlyDictionary<string, ulong[]> moduleMethodPointers,
        Rosetta.Metadata.MetadataParser metadata)
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"RuntimeHelperScanner.ProbeUnresolvedUserMethods()");
        var unresolvedCounts = _unresolvedCallCounts;
        if (unresolvedCounts == null || unresolvedCounts.Count == 0) return 0;

        // Collect ALL remaining unresolved targets (no frequency threshold)
        var remainingUnresolved = new HashSet<ulong>();
        foreach (var (addr, count) in unresolvedCounts)
        {
            if (_callResolver.TryResolve(addr) == null)
                remainingUnresolved.Add(addr);
        }

        if (remainingUnresolved.Count == 0) return 0;

        // Build a flat VA → (moduleName, pointerIndex) lookup from all modules
        var vaToModuleIndex = new Dictionary<ulong, (string module, int index)>();
        foreach (var (moduleName, pointers) in moduleMethodPointers)
        {
            for (int j = 0; j < pointers.Length; j++)
            {
                ulong ptr = pointers[j];
                if (ptr == 0) continue;
                if (_isArm32) ptr &= ~1UL; // Strip Thumb bit
                if (remainingUnresolved.Contains(ptr))
                {
                    vaToModuleIndex.TryAdd(ptr, (moduleName, j));
                }
            }
        }

        if (vaToModuleIndex.Count == 0) return 0;

        // For each found VA, resolve the method name from the module's RID-ordered method list
        int resolved = 0;
        foreach (var (va, (moduleName, ptrIndex)) in vaToModuleIndex)
        {
            // Find the matching image
            Rosetta.Metadata.ImageDefinition? image = null;
            for (int i = 0; i < metadata.ImageDefinitions.Length; i++)
            {
                if (metadata.ImageDefinitions[i].Name == moduleName)
                {
                    image = metadata.ImageDefinitions[i];
                    break;
                }
            }
            if (image == null) continue;

            // Collect method indices in RID order (same logic as bridge)
            var methodIndices = new List<int>();
            int typeEnd = image.TypeStart + (int)image.TypeCount;
            for (int typeIdx = image.TypeStart;
                 typeIdx < typeEnd && typeIdx < metadata.TypeDefinitions.Length;
                 typeIdx++)
            {
                if (typeIdx < 0) continue;
                var td = metadata.TypeDefinitions[typeIdx];
                for (int m = 0; m < td.MethodCount; m++)
                {
                    int idx = td.MethodStart + m;
                    if (idx >= 0 && idx < metadata.MethodDefinitions.Length)
                        methodIndices.Add(idx);
                }
            }

            // Sort by RID (critical — must match pointer array ordering)
            methodIndices.Sort((a, b) =>
            {
                uint ridA = metadata.MethodDefinitions[a].Token & 0x00FFFFFF;
                uint ridB = metadata.MethodDefinitions[b].Token & 0x00FFFFFF;
                return ridA.CompareTo(ridB);
            });

            // Map pointer index to method
            if (ptrIndex < methodIndices.Count)
            {
                int methodIdx = methodIndices[ptrIndex];
                var md = metadata.MethodDefinitions[methodIdx];
                string name = md.Name ?? $"Method_{methodIdx}";

                string? typeName = null;
                if (md.DeclaringTypeIndex >= 0 && md.DeclaringTypeIndex < metadata.TypeDefinitions.Length)
                    typeName = metadata.TypeDefinitions[md.DeclaringTypeIndex].FullName;

                string fullName = typeName != null ? $"{typeName}::{name}" : name;

                _callResolver.RegisterRuntimeFunction(va, fullName);
                resolved++;
            }
        }

        if (resolved > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Probe: resolved {resolved} additional user methods from module pointer tables");
            Console.ResetColor();
        }

        return resolved;
    }

    /// <summary>
    /// Final pass: resolve linker veneers and label remaining unknowns.
    ///
    /// Linker veneers are single-instruction B (branch) stubs generated when
    /// a BL target is too far for the 26-bit immediate. They redirect to
    /// the actual function. Universal across all AArch64 ELF binaries.
    ///
    /// After veneer resolution, any truly unresolved targets get labeled
    /// as sub_XXXXXX (standard reverse-engineering convention).
    ///
    /// Returns the number of targets resolved or labeled.
    /// </summary>
    public int LabelRemainingUnknowns()
    {
        if (ConsoleReporter.IsTracing) ConsoleReporter.Trace($"RuntimeHelperScanner.LabelRemainingUnknowns()");
        var unresolvedCounts = _unresolvedCallCounts;
        if (unresolvedCounts == null || unresolvedCounts.Count == 0) return 0;

        int veneersResolved = 0;
        int labeled = 0;

        // Collect unresolved addresses first (avoid modifying resolver during iteration)
        var unresolved = new List<ulong>();
        foreach (var (addr, count) in unresolvedCounts)
        {
            if (_callResolver.TryResolve(addr) == null)
                unresolved.Add(addr);
        }

        foreach (var addr in unresolved)
        {
            if (_callResolver.TryResolve(addr) != null) continue;

            long fileOffset = _elf.VirtualToFileOffset(addr);

            if (_isArm32)
            {
                // ARM32: veneers are Thumb2 (MOVW/MOVT/ADD/BX) — already followed during scan.
                // Just try PLT lookup for any remaining targets.
                if (_elf.PltStubSymbols.TryGetValue(addr, out string? pltSym))
                {
                    _callResolver.RegisterRuntimeFunction(addr, pltSym);
                    veneersResolved++;
                    continue;
                }
            }
            else
            {
                // ARM64: check for single-B linker veneers
                if (fileOffset >= 0 && fileOffset + 4 <= _binaryData.Length)
                {
                    var span = _binaryData.Span;
                    uint raw = (uint)(span[(int)fileOffset] | (span[(int)fileOffset + 1] << 8) |
                                      (span[(int)fileOffset + 2] << 16) | (span[(int)fileOffset + 3] << 24));

                    // B (unconditional branch): bits [31:26] = 000101
                    if ((raw >> 26) == 0b000101)
                    {
                        int imm26 = (int)(raw & 0x03FFFFFF);
                        if ((imm26 & 0x02000000) != 0) imm26 |= unchecked((int)0xFC000000);
                        ulong realTarget = addr + (ulong)(imm26 << 2);

                        bool resolved = false;
                        for (int depth = 0; depth < 3; depth++)
                        {
                            var resolvedCall = _callResolver.TryResolve(realTarget);
                            if (resolvedCall != null)
                            {
                                string label = resolvedCall.DeclaringType != null
                                    ? $"{resolvedCall.DeclaringType}::{resolvedCall.MethodName}"
                                    : resolvedCall.MethodName ?? $"sub_{realTarget:X}";
                                _callResolver.RegisterRuntimeFunction(addr, label);
                                veneersResolved++;
                                resolved = true;
                                break;
                            }

                            long rtOff = _elf.VirtualToFileOffset(realTarget);
                            if (rtOff < 0 || rtOff + 4 > _binaryData.Length) break;
                            uint rawRT = (uint)(span[(int)rtOff] | (span[(int)rtOff + 1] << 8) |
                                               (span[(int)rtOff + 2] << 16) | (span[(int)rtOff + 3] << 24));
                            if ((rawRT >> 26) != 0b000101) break;

                            int immRT = (int)(rawRT & 0x03FFFFFF);
                            if ((immRT & 0x02000000) != 0) immRT |= unchecked((int)0xFC000000);
                            realTarget = realTarget + (ulong)(immRT << 2);
                        }

                        if (resolved) continue;
                    }
                }
            }

            // Standard sub_ label
            _callResolver.RegisterRuntimeFunction(addr, $"sub_{addr:X}");
            labeled++;
        }

        if (veneersResolved > 0 || labeled > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            if (veneersResolved > 0)
                Console.WriteLine($"  Veneers resolved: {veneersResolved}");
            if (labeled > 0)
                Console.WriteLine($"  Labeled {labeled} unresolved targets as sub_XXXXXX");
            Console.ResetColor();
        }

        return veneersResolved + labeled;
    }
}
