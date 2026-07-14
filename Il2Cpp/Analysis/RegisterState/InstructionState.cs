using System;
using System.Collections.Generic;

namespace Rosetta.Analysis.RegisterState;

/// <summary>
/// Snapshot of all register states at a single instruction point.
/// </summary>
public sealed class InstructionState
{
    /// <summary>GP register states (index 0-30).</summary>
    public RegInfo?[] GpRegs { get; } = new RegInfo?[31];

    /// <summary>SP-relative store values: key = offset, value = what was stored.</summary>
    public Dictionary<long, RegInfo> SpSlots { get; } = new();

    /// <summary>Deep-copy the state for branching.</summary>
    public InstructionState Clone()
    {
        var copy = new InstructionState();
        Array.Copy(GpRegs, copy.GpRegs, 31);
        foreach (var kv in SpSlots)
            copy.SpSlots[kv.Key] = kv.Value;
        return copy;
    }
}
