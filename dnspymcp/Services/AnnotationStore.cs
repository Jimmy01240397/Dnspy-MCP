using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnSpyMcp.Services;

/// <summary>
/// Per-assembly sidecar store for user annotations (renames + comments).
/// Persists to <c>&lt;asm_path&gt;.dnspymcp.json</c> next to the assembly so
/// the metadata travels with the file and survives MCP-server restarts.
///
/// Annotations are MCP-visible only: they don't rewrite decompiler output
/// or modify the on-disk PE/MD. They live alongside the assembly as a
/// curator's notebook — useful for "remember what I learned about token
/// 0x06000123" workflows during long reverse-engineering sessions.
/// Per-asm — opening the same DLL again reloads them.
/// </summary>
public sealed class AnnotationStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<uint, string> _renames = new();
    private readonly ConcurrentDictionary<uint, string> _comments = new();

    public AnnotationStore(string assemblyPath)
    {
        _path = assemblyPath + ".dnspymcp.json";
        Load();
    }

    public string SidecarPath => _path;

    public string? GetRename(uint token) => _renames.TryGetValue(token, out var n) ? n : null;
    public string? GetComment(uint token) => _comments.TryGetValue(token, out var c) ? c : null;

    public void SetRename(uint token, string newName)
    {
        _renames[token] = newName;
        Save();
    }

    public void SetComment(uint token, string text)
    {
        _comments[token] = text;
        Save();
    }

    public bool ClearRename(uint token)
    {
        var removed = _renames.TryRemove(token, out _);
        if (removed) Save();
        return removed;
    }

    public bool ClearComment(uint token)
    {
        var removed = _comments.TryRemove(token, out _);
        if (removed) Save();
        return removed;
    }

    public IEnumerable<(uint Token, string Value)> AllRenames() => _renames.Select(kv => (kv.Key, kv.Value));
    public IEnumerable<(uint Token, string Value)> AllComments() => _comments.Select(kv => (kv.Key, kv.Value));

    private sealed class FileShape
    {
        [JsonPropertyName("renames")] public Dictionary<string, string> Renames { get; set; } = new();
        [JsonPropertyName("comments")] public Dictionary<string, string> Comments { get; set; } = new();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var shape = JsonSerializer.Deserialize<FileShape>(json);
            if (shape == null) return;
            foreach (var (k, v) in shape.Renames)
                if (uint.TryParse(k, out var tok)) _renames[tok] = v;
            foreach (var (k, v) in shape.Comments)
                if (uint.TryParse(k, out var tok)) _comments[tok] = v;
        }
        catch
        {
            // Sidecar is best-effort; a corrupt file shouldn't block opening
            // the assembly. The next save will overwrite it cleanly.
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            var shape = new FileShape();
            foreach (var (tok, name) in _renames)
                shape.Renames[tok.ToString()] = name;
            foreach (var (tok, txt) in _comments)
                shape.Comments[tok.ToString()] = txt;
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(shape, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Read-only filesystem / permission issue. Annotations stay
                // in-memory — caller will hit the same error next time.
            }
        }
    }
}
