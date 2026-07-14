using System.Collections.Generic;
using System.Linq;

namespace Rosetta.Model;

internal sealed class EnumInfo
{
    private readonly string _typeName;
    private readonly Dictionary<long, string> _exact = new();
    private readonly List<EnumValueEntry> _flagEntries;
    private readonly bool _hasFlagsAttribute;

    public EnumInfo(string typeName, List<EnumValueEntry> entries, bool hasFlagsAttribute)
    {
        _typeName = typeName;
        _hasFlagsAttribute = hasFlagsAttribute;
        foreach (var entry in entries)
            _exact.TryAdd(entry.Value, entry.Name);

        _flagEntries = entries
            .Where(e => e.Value > 0 && (e.Value & (e.Value - 1)) == 0)
            .ToList();
    }

    public string? Resolve(long value)
    {
        if (_exact.TryGetValue(value, out string? fieldName))
            return $"{_typeName}.{fieldName}";

        if (!_hasFlagsAttribute || value <= 0 || _flagEntries.Count == 0)
            return null;

        long remaining = value;
        var names = new List<string>();
        foreach (var entry in _flagEntries)
        {
            if ((remaining & entry.Value) == entry.Value)
            {
                names.Add($"{_typeName}.{entry.Name}");
                remaining &= ~entry.Value;
            }
        }

        return remaining == 0 && names.Count > 0
            ? string.Join(" | ", names)
            : null;
    }
}

internal readonly record struct EnumValueEntry(string Name, long Value);
