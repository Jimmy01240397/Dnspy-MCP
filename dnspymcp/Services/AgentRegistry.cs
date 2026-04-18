using System.Collections.Concurrent;

namespace DnSpyMcp.Services;

/// <summary>
/// Holds N named <see cref="AgentClient"/> instances so the MCP server can talk
/// to several dnspymcpagent processes at once (e.g. one per target VM). Each
/// tool call operates on the <em>active</em> agent unless a <c>name</c> is passed
/// explicitly.
///
/// Thread safety: the agent map is a ConcurrentDictionary; the active-name slot
/// is guarded by a small lock. AgentClient itself serialises its own IO.
/// </summary>
public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentClient> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _activeLock = new();
    private string? _active;

    public AgentClient Default { get; }

    public AgentRegistry()
    {
        // keep a single, always-present "default" agent for backwards compatibility
        Default = new AgentClient();
        _agents["default"] = Default;
        _active = "default";
    }

    public string? ActiveName
    {
        get { lock (_activeLock) return _active; }
    }

    /// <summary>Resolve the agent to use. If <paramref name="name"/> is null/empty, use the active one.</summary>
    public AgentClient Get(string? name = null)
    {
        if (string.IsNullOrEmpty(name))
        {
            string? n;
            lock (_activeLock) n = _active;
            if (n == null) throw new InvalidOperationException("no active agent. Call live_agent_connect first.");
            if (!_agents.TryGetValue(n, out var a))
                throw new InvalidOperationException($"active agent '{n}' is gone (was it removed?).");
            return a;
        }
        if (!_agents.TryGetValue(name, out var agent))
            throw new InvalidOperationException($"agent '{name}' is not registered. Call live_agent_connect with this name.");
        return agent;
    }

    /// <summary>
    /// Get an existing named agent or create one. Newly-created agents are not
    /// yet connected — the caller must Configure+Connect.
    /// </summary>
    public AgentClient GetOrCreate(string name)
    {
        return _agents.GetOrAdd(name, _ => new AgentClient());
    }

    public bool Remove(string name)
    {
        if (_agents.TryRemove(name, out var a))
        {
            try { a.Dispose(); } catch { /* ignore */ }
            lock (_activeLock)
            {
                if (string.Equals(_active, name, StringComparison.OrdinalIgnoreCase))
                    _active = _agents.Keys.FirstOrDefault();
            }
            return true;
        }
        return false;
    }

    public AgentClient Switch(string name)
    {
        if (!_agents.TryGetValue(name, out var a))
            throw new InvalidOperationException($"agent '{name}' is not registered. Call live_agent_connect with this name first.");
        lock (_activeLock) _active = name;
        return a;
    }

    public void SetActive(string name)
    {
        lock (_activeLock) _active = name;
    }

    public IEnumerable<KeyValuePair<string, AgentClient>> All => _agents;
}
