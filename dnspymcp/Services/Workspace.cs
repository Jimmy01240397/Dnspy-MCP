using System.Collections.Concurrent;
using dnlib.DotNet;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

namespace DnSpyMcp.Services;

/// <summary>
/// Holds the set of on-disk assemblies opened for FILE (static) analysis, plus
/// a shared decompiler settings bundle.  Mirrors the "idalib" model used by
/// the IDA MCP: you open N files, each gets a short handle (path) you can
/// reference in every tool call.
///
/// Keys are absolute file paths. One asm_file is one .dll / .exe.
/// </summary>
public sealed class Workspace
{
    public sealed class OpenedAsm(string path, ModuleDefMD module, CSharpDecompiler decompiler, PEFile peFile)
    {
        public string Path { get; } = path;
        public ModuleDefMD Module { get; } = module;
        public CSharpDecompiler Decompiler { get; } = decompiler;
        public PEFile PEFile { get; } = peFile;
    }

    private readonly ConcurrentDictionary<string, OpenedAsm> _open = new(System.StringComparer.OrdinalIgnoreCase);
    public DecompilerSettings Settings { get; } = new(LanguageVersion.CSharp11_0)
    {
        UsingDeclarations = true,
        ShowXmlDocumentation = false,
        AggressiveScalarReplacementOfAggregates = false,
    };

    public OpenedAsm Open(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        return _open.GetOrAdd(path, p =>
        {
            var pe = new PEFile(p);
            var module = ModuleDefMD.Load(p);
            var resolver = new UniversalAssemblyResolver(p, true, pe.Metadata.DetectTargetFrameworkId());
            var decomp = new CSharpDecompiler(p, resolver, Settings);
            return new OpenedAsm(p, module, decomp, pe);
        });
    }

    public bool Close(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (_open.TryRemove(path, out var a))
        {
            try { a.Module.Dispose(); } catch { }
            return true;
        }
        return false;
    }

    public OpenedAsm Get(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!_open.TryGetValue(path, out var a))
            throw new System.InvalidOperationException($"asm_file not opened: {path}. Call asm_file_open first.");
        return a;
    }

    public IEnumerable<OpenedAsm> All => _open.Values;
}
