using System;
using Gee.External.Capstone;
using Gee.External.Capstone.Arm64;
using Gee.External.Capstone.Arm;

namespace Rosetta.Binary;

/// <summary>
/// Thin wrapper around Capstone ARM64 and ARM32 disassemblers for comparison purposes.
/// Creates a single disassembler instance that can be reused for multiple methods.
///
/// Thread safety: NOT thread-safe. Each thread in the parallel pipeline must
/// create its own instance (thread-local pattern in AssemblyPipeline).
///
/// Implements IDisposable to properly release the native Capstone handle.
/// </summary>
public sealed class CapstoneDisassemblerWrapper : IDisposable
{
    private readonly CapstoneArm64Disassembler? _disassembler64;
    private readonly CapstoneArmDisassembler? _disassembler32Thumb;
    private readonly CapstoneArmDisassembler? _disassembler32Arm;
    private bool _disposed;

    public CapstoneDisassemblerWrapper(bool isArm32 = false)
    {
        if (isArm32)
        {
            _disassembler32Thumb = CapstoneDisassembler.CreateArmDisassembler(ArmDisassembleMode.Thumb);
            _disassembler32Thumb.EnableInstructionDetails = false;
            _disassembler32Arm = CapstoneDisassembler.CreateArmDisassembler(ArmDisassembleMode.Arm);
            _disassembler32Arm.EnableInstructionDetails = false;
        }
        else
        {
            _disassembler64 = CapstoneDisassembler.CreateArm64Disassembler(Arm64DisassembleMode.Arm);
            _disassembler64.EnableInstructionDetails = false;
        }
    }

    /// <summary>
    /// Disassemble a block of native code and return per-instruction text.
    /// For ARM32, defaults to Thumb mode (legacy behavior).
    /// Use <see cref="DisassembleBlockArm"/> for ARM mode.
    /// </summary>
    public CapstoneInstruction[] DisassembleBlock(byte[] data, ulong baseVA)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CapstoneDisassemblerWrapper));
        if (data.Length == 0) return [];

        if (_disassembler32Thumb != null)
        {
            return DisassembleWithArm32(_disassembler32Thumb, data, baseVA);
        }
        else if (_disassembler64 != null)
        {
            var instructions = _disassembler64.Disassemble(data, (long)baseVA);
            var result = new CapstoneInstruction[instructions.Length];
            for (int i = 0; i < instructions.Length; i++)
            {
                var insn = instructions[i];
                result[i] = new CapstoneInstruction(
                    (ulong)insn.Address,
                    insn.Mnemonic ?? "",
                    insn.Operand ?? "");
            }
            return result;
        }

        return [];
    }

    /// <summary>
    /// Disassemble a block in ARM mode (32-bit fixed-width instructions).
    /// For methods whose raw pointer had bit 0 = 0.
    /// </summary>
    public CapstoneInstruction[] DisassembleBlockArm(byte[] data, ulong baseVA)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CapstoneDisassemblerWrapper));
        if (data.Length == 0) return [];
        if (_disassembler32Arm == null) return [];

        return DisassembleWithArm32(_disassembler32Arm, data, baseVA);
    }

    private static CapstoneInstruction[] DisassembleWithArm32(CapstoneArmDisassembler disasm, byte[] data, ulong baseVA)
    {
        var instructions = disasm.Disassemble(data, (long)baseVA);
        var result = new CapstoneInstruction[instructions.Length];
        for (int i = 0; i < instructions.Length; i++)
        {
            var insn = instructions[i];
            result[i] = new CapstoneInstruction(
                (ulong)insn.Address,
                insn.Mnemonic ?? "",
                insn.Operand ?? "");
        }
        return result;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disassembler64?.Dispose();
            _disassembler32Thumb?.Dispose();
            _disassembler32Arm?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// A single Capstone-disassembled instruction for comparison.
/// </summary>
public readonly struct CapstoneInstruction
{
    public readonly ulong Address;
    public readonly string Mnemonic;
    public readonly string Operands;

    public CapstoneInstruction(ulong address, string mnemonic, string operands)
    {
        Address = address;
        Mnemonic = mnemonic;
        Operands = operands;
    }

    /// <summary>Format as "MNEMONIC operands" for display/comparison.</summary>
    public string ToDisplayString() =>
        string.IsNullOrEmpty(Operands) ? Mnemonic : $"{Mnemonic} {Operands}";
}
