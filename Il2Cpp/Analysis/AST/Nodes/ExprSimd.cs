using System;

namespace Rosetta.Analysis.AST;

/// <summary>
/// Represents a 128-bit SIMD value (Q-register) natively in the AST.
/// Preserves exact 128-bit raw values without relying on string conversion.
/// </summary>
public sealed class ExprSimd : ExprNode
{
    public long RawLo { get; }
    public long RawHi { get; }

    public ExprSimd(long rawLo, long rawHi)
    {
        RawLo = rawLo;
        RawHi = rawHi;
    }

    public override string Emit()
    {
        if (RawHi == 0 && RawLo == 0) return "default";
        
        // Emit as a synthetic syntax or comment, mostly for debugging
        // Typically, StoreBuilder consumes this node directly to split it.
        return $"simd_128(0x{(ulong)RawHi:X16}, 0x{(ulong)RawLo:X16})";
    }

    /// <summary>
    /// Extract the four 32-bit uint slots (used by StoreBuilder for Vector4/Quaternion recovery).
    /// Slot 0 is the lowest 32 bits of RawLo.
    /// </summary>
    public uint[] GetSlots32()
    {
        return new uint[]
        {
            (uint)RawLo,
            (uint)(RawLo >> 32),
            (uint)RawHi,
            (uint)(RawHi >> 32)
        };
    }
}
