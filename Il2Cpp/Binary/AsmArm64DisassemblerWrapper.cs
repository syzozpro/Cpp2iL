using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AsmArm64;

namespace Rosetta.Binary;

/// <summary>
/// Wrapper around xoofx/AsmArm64 for ARM64 disassembly comparison.
/// Pure .NET, auto-generated from official ARM XML specs, 2448+ instructions.
///
/// Unlike Capstone, AsmArm64 is a managed .NET library with no native dependencies.
/// It provides the most spec-accurate disassembly of any .NET ARM64 library.
///
/// Thread safety: Instances are NOT thread-safe. Each thread needs its own instance.
/// </summary>
public sealed class AsmArm64DisassemblerWrapper
{
    private readonly Arm64Disassembler _disassembler;

    public AsmArm64DisassemblerWrapper()
    {
        _disassembler = new Arm64Disassembler(new Arm64DisassemblerOptions
        {
            PrintAddress = false,
            PrintAssemblyBytes = false,
            PrintNewLineAfterBranch = false,
            PrintNewLineBeforeLabel = false,
            PrintLabelBeforeFirstInstruction = false,
            IndentSize = 0,
        });
    }

    /// <summary>
    /// Disassemble a block of ARM64 code and return per-instruction text.
    /// Returns an array of (Address, DisasmText) tuples, one per 4-byte instruction.
    /// </summary>
    /// <param name="data">Raw instruction bytes.</param>
    /// <param name="baseVA">Virtual address of the first byte.</param>
    /// <returns>Array of disassembled instruction tuples.</returns>
    public AsmArm64Instruction[] DisassembleBlock(byte[] data, ulong baseVA)
    {
        if (data.Length == 0) return [];

        // AsmArm64 disassembles the whole block to a string; we parse per-line
        _disassembler.Options.BaseAddress = (long)baseVA;

        string fullText = _disassembler.Disassemble(data);
        var lines = fullText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        int instCount = data.Length / 4;
        var result = new List<AsmArm64Instruction>(instCount);

        // Each line is one instruction (no address prefix due to PrintAddress=false)
        // Lines may contain labels like "L_XXXX:" which we skip
        int instrIdx = 0;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Skip label lines (end with ':')
            if (line.EndsWith(':')) continue;

            // Calculate address from instruction index
            if (instrIdx < instCount)
            {
                ulong addr = baseVA + (ulong)(instrIdx * 4);
                result.Add(new AsmArm64Instruction(addr, line));
                instrIdx++;
            }
        }

        return result.ToArray();
    }
}

/// <summary>
/// A single AsmArm64-disassembled instruction.
/// </summary>
public readonly struct AsmArm64Instruction
{
    public readonly ulong Address;
    public readonly string Text;

    public AsmArm64Instruction(ulong address, string text)
    {
        Address = address;
        Text = text;
    }
}
