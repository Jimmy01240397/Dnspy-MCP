using System.Collections.Concurrent;
using dnlib.DotNet;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ModelContextProtocol;

namespace DnSpyMcp.Services;

/// <summary>
/// Holds the set of on-disk assemblies opened for FILE (static) analysis, plus
/// a shared decompiler settings bundle.  Mirrors the "idalib" model used by
/// the IDA MCP: you open N files, each gets a short handle (path) you can
/// reference in every tool call.
///
/// Keys are absolute file paths. One asm_file is one .dll / .exe.
/// </summary>
public sealed class Workspace : IDisposable
{
    public sealed class OpenedAsm(string path, ModuleDefMD module, CSharpDecompiler decompiler, PEFile peFile)
    {
        public string Path { get; } = path;
        public ModuleDefMD Module { get; } = module;
        public CSharpDecompiler Decompiler { get; } = decompiler;
        public PEFile PEFile { get; } = peFile;
        // Per-asm sidecar JSON of user annotations (renames + comments).
        // Loaded on first access; persisted on every mutation. Lives next
        // to the file so it survives MCP-server restarts.
        public AnnotationStore Annotations { get; } = new(path);
    }

    private readonly ConcurrentDictionary<string, OpenedAsm> _open = new(System.StringComparer.OrdinalIgnoreCase);
    private string? _current;
    // Cross-DLL search index. Populated incrementally by Open, pruned by Close.
    // Tools that support cross-DLL search (find_string, xref_to_method, ...)
    // query this instead of walking Module.GetTypes() per call.
    internal readonly CrossDllIndex Index = new();
    public DecompilerSettings Settings { get; } = new(LanguageVersion.CSharp11_0)
    {
        UsingDeclarations = true,
        ShowXmlDocumentation = false,
        AggressiveScalarReplacementOfAggregates = false,
    };

    public OpenedAsm Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        // Refuse to open the same asm twice. Silently returning the existing
        // instance let callers accumulate invisible duplicates in their
        // session list; an explicit error makes the state mismatch visible
        // so they can either switch to the existing slot or close it first.
        if (_open.TryGetValue(path, out var existing))
            throw new McpException($"asm_file already opened: {path}. Call reverse_switch to make it active, or reverse_close to free it first.");
        var pe = new PEFile(path);
        var module = ModuleDefMD.Load(path);
        var resolver = new UniversalAssemblyResolver(path, true, pe.Metadata.DetectTargetFrameworkId());
        // Share the PEFile with the decompiler so Close() only needs to
        // dispose one mapping. Passing the path would make the decompiler
        // open its own PEFile and leak the file handle.
        var decomp = new CSharpDecompiler(pe, resolver, Settings);
        var opened = new OpenedAsm(path, module, decomp, pe);
        if (!_open.TryAdd(path, opened))
        {
            // Lost a race against a concurrent Open of the same path; clean up
            // our half-built PEFile/module and tell the caller which asm won.
            try { module.Dispose(); } catch { }
            try { pe.Dispose(); } catch { }
            throw new McpException($"asm_file already opened: {path} (concurrent open race). Call reverse_switch to make it active.");
        }
        // Eagerly index on open. This moves the cost of future cross-DLL
        // find_string / xref_to_method queries from per-query back to
        // per-open — matters when the caller opens ~1000 GAC DLLs.
        Index.Add(path, module);
        _current = path;
        return opened;
    }

    public bool Close(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (_open.TryRemove(path, out var a))
        {
            Index.Remove(path);
            // Release every resource that might still map the file on disk,
            // otherwise the file stays locked after close.
            try { a.Module.Dispose(); } catch { }
            try { a.PEFile.Dispose(); } catch { }
            if (string.Equals(_current, path, System.StringComparison.OrdinalIgnoreCase))
                _current = _open.Keys.FirstOrDefault();
            return true;
        }
        return false;
    }

    /// <summary>Resolve the asm to use. If path is null/empty, use the currently-active one.</summary>
    public OpenedAsm Get(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            if (_current == null || !_open.TryGetValue(_current, out var cur))
                throw new McpException("no active asm_file. Open one via asm_file_open or set it via asm_file_switch.");
            return cur;
        }
        var full = System.IO.Path.GetFullPath(path);
        if (!_open.TryGetValue(full, out var a))
            throw new McpException($"asm_file not opened: {full}. Call asm_file_open first.");
        return a;
    }

    public string? Current => _current;

    public OpenedAsm Switch(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!_open.TryGetValue(full, out var a))
            throw new McpException($"asm_file not opened: {full}. Call asm_file_open first.");
        _current = full;
        return a;
    }

    public IEnumerable<OpenedAsm> All => _open.Values;

    /// <summary>
    /// Close every opened asm — called by the DI container when the host shuts
    /// down so on-disk files never stay locked after MCP exits.
    /// </summary>
    public void Dispose()
    {
        foreach (var path in _open.Keys.ToArray())
            Close(path);
    }
}
