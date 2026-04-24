using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;

namespace DnSpyMcp.Services;

/// <summary>
/// Minimal <see cref="IDsDocumentService"/> adapter over our
/// <see cref="Workspace"/>. Exists purely so we can hand a document list to
/// dnSpy's <c>ScopedWhereUsedAnalyzer&lt;T&gt;</c> without dragging in the
/// dnSpy UI runtime.
///
/// Only <see cref="GetDocuments"/> and <see cref="AssemblyResolver"/> are
/// actually called by the analyzer path (ScopedWhereUsedAnalyzer +
/// SearchNode helpers it uses). Every other <see cref="IDsDocumentService"/>
/// method belongs to the WPF document-tree manager we don't run, so they
/// throw — if a future code path in the analyzer starts calling one, we'll
/// see a real stack trace instead of silent misbehavior.
/// </summary>
internal sealed class WorkspaceDocumentService : IDsDocumentService
{
    private readonly Workspace _workspace;

    public WorkspaceDocumentService(Workspace workspace) => _workspace = workspace;

    public IDsDocument[] GetDocuments()
        => _workspace.All
            .Where(a => a.Module is not null)
            .Select(a => (IDsDocument)DsDotNetDocument.CreateAssembly(
                DsDocumentInfo.CreateDocument(a.Path), a.Module!, loadSyms: false))
            .ToArray();

    public IAssemblyResolver AssemblyResolver => _assemblyResolver ??= new TheAssemblyResolver();
    private IAssemblyResolver? _assemblyResolver;

    // Pass-through resolver that just walks the current workspace's open
    // modules looking for a name match. No disk probing.
    private sealed class TheAssemblyResolver : IAssemblyResolver
    {
        public AssemblyDef? Resolve(IAssembly assembly, ModuleDef sourceModule) => null;
    }

    // ---- IDsDocumentService stubs --------------------------------------
    // These are the rest of the IDsDocumentService surface — none of them is
    // exercised by ScopedWhereUsedAnalyzer or the SearchNode helpers it
    // calls. C# requires implementations for all interface members, so we
    // either return null/no-op (queries) or throw (mutations). The throws
    // are deliberate trip wires: if a future dnSpy.Analyzer.x revision
    // starts calling one of these, the stack trace tells us exactly which
    // member to implement instead of the call silently misbehaving.

    public event System.EventHandler<NotifyDocumentCollectionChangedEventArgs>? CollectionChanged
    {
        add { /* never raised */ }
        remove { }
    }
    public System.IDisposable DisableAssemblyLoad() => NullDisposable.Instance;
    public IDsDocument GetOrAdd(IDsDocument document) => throw NotUsed();
    public IDsDocument ForceAdd(IDsDocument document, bool delayLoad, object? data) => throw NotUsed();
    public IDsDocument? TryGetOrCreate(DsDocumentInfo info, bool isAutoLoaded = false) => null;
    public IDsDocument? TryCreateOnly(DsDocumentInfo info) => null;
    public IDsDocument? Resolve(IAssembly asm, ModuleDef? sourceModule) => null;
    public IDsDocument? FindAssembly(IAssembly assembly) => null;
    public IDsDocument? FindAssembly(IAssembly assembly, FindAssemblyOptions options) => null;
    public IDsDocument? Find(IDsDocumentNameKey key) => null;
    public void Remove(IDsDocumentNameKey key) => throw NotUsed();
    public void Remove(IEnumerable<IDsDocument> documents) => throw NotUsed();
    public void Clear() => throw NotUsed();
    public void SetDispatcher(System.Action<System.Action> action) { /* never installed */ }
    public IDsDocument CreateDocument(DsDocumentInfo documentInfo, string filename, bool isModule = false) => throw NotUsed();
    public IDsDocument CreateDocument(DsDocumentInfo documentInfo, byte[] fileData, string? filename, bool isFileLayout, bool isModule = false) => throw NotUsed();

    private static System.NotSupportedException NotUsed([System.Runtime.CompilerServices.CallerMemberName] string? member = null)
        => new($"WorkspaceDocumentService.{member} is unused by the analyzer code path. " +
               "If you're seeing this, dnSpy.Analyzer.x grew a new dependency — wire it through Workspace.");

    private sealed class NullDisposable : System.IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}
