using System.Collections.Generic;
using dndbg.Engine;

namespace DnSpyMcp.Agent.Services;

public sealed class BreakpointEntry
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";  // il, native, by_name
    public string Description { get; set; } = "";
    public object DnBreakpoint { get; set; } = default!;
    public bool Enabled { get; set; } = true;

    // Conditional-BP state (D2). Condition is the user-supplied source
    // expression, kept verbatim for `bp.list` display. HitCount increments
    // on every callback firing — both when the condition holds and when
    // it doesn't, so the count is always the true number of times the
    // instruction was hit.
    public string? Condition { get; set; }
    public int HitCount;  // public field so the condition callback can Interlocked.Increment it
}

public sealed class BreakpointRegistry
{
    private int _nextId = 1;
    private readonly Dictionary<int, BreakpointEntry> _map = new();

    public BreakpointEntry Add(string kind, string description, object dnBreakpoint)
    {
        var entry = new BreakpointEntry
        {
            Id = _nextId++,
            Kind = kind,
            Description = description,
            DnBreakpoint = dnBreakpoint,
            Enabled = true,
        };
        _map[entry.Id] = entry;
        return entry;
    }

    public bool TryGet(int id, out BreakpointEntry entry) => _map.TryGetValue(id, out entry!);
    public bool Remove(int id) => _map.Remove(id);
    public IEnumerable<BreakpointEntry> All => _map.Values;
    public void Clear() => _map.Clear();
}
