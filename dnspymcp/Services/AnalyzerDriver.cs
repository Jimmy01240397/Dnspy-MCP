using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using dnSpy.Analyzer;
using dnSpy.Analyzer.TreeNodes;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.TreeView;
using dnSpy.Contracts.TreeView.Text;

namespace DnSpyMcp.Services;

/// <summary>
/// Drives any of dnSpy's analyzer <see cref="SearchNode"/> subclasses (the
/// "Used By" / "Instantiations" / "Overrides" / etc tree-node classes that
/// live in <c>dnSpy.Analyzer.x.dll</c>) without dragging in the WPF runtime.
///
/// Each analyzer node implements the per-query reflection logic for one
/// specific question ("which methods call X", "which types derive from X",
/// "which methods access field X", ...) and feeds the shared cross-DLL
/// engine via <c>ScopedWhereUsedAnalyzer&lt;T&gt;</c>. By instantiating
/// the dnSpy node directly and pulling its <c>FetchChildrenInternal</c> we
/// reuse the production analyzer end-to-end — TypeRef pre-filter,
/// accessibility scoping, friend-assembly handling, type-equivalence,
/// COM interop, custom-attribute walking — without copying a line of
/// dnSpy source. The tree-view bits the node also touches at display time
/// are stubbed by <see cref="MinimalContext"/> below.
/// </summary>
internal static class AnalyzerDriver
{
    /// <summary>
    /// Run a constructed analyzer node and yield every <see cref="EntityNode"/>
    /// it produces. Caller supplies the dnSpy node already initialized with
    /// the target dnlib member; we plug in a Workspace-backed context and
    /// drive <c>FetchChildrenInternal</c>. Other (non-EntityNode) result
    /// shapes — <c>AssemblyNode</c> / <c>ModuleNode</c> — are skipped
    /// because they're surface dressing for the GUI tree, not concrete
    /// usage sites we want to surface in MCP output.
    /// </summary>
    public static IEnumerable<EntityNode> Drive(SearchNode node, Workspace ws, CancellationToken ct)
    {
        node.Context = new MinimalContext(new WorkspaceDocumentService(ws));
        foreach (var child in node.FetchChildrenInternal(ct))
        {
            if (child is EntityNode en) yield return en;
        }
    }

    /// <summary>
    /// Stub <see cref="IAnalyzerTreeNodeDataContext"/> that supplies only
    /// <see cref="DocumentService"/>. Everything else is exclusively used by
    /// the WPF tree (icons, decompiler colorization, click handling) which
    /// we never run. The other members are wired as null / no-op so a
    /// future change in dnSpy that touches them throws loud rather than
    /// rendering silently broken output.
    /// </summary>
    private sealed class MinimalContext : IAnalyzerTreeNodeDataContext
    {
        public MinimalContext(IDsDocumentService docs) => DocumentService = docs;
        public IDsDocumentService DocumentService { get; }

        // Tree / image / analyzer services are only touched by the WPF layer
        // when rendering the analyzer panel — never by FetchChildren.
        public IDotNetImageService DotNetImageService => null!;
        public IAnalyzerService AnalyzerService => null!;
        public ITreeView TreeView => null!;
        public ITreeViewNodeTextElementProvider TreeViewNodeTextElementProvider => null!;
        public bool ShowToken => false;
        public bool SingleClickExpandsChildren => false;
        public bool SyntaxHighlight => false;

        // Decompiler IS read indirectly: FieldAccessNode / EventFiredByNode /
        // AttributeAppliedToNode call AnalyzerTreeNodeData.GetOriginalCodeLocation,
        // which dereferences Context.Decompiler.UniqueGuid. The early-return
        // is `if (UniqueGuid != LANGUAGE_CSHARP_ILSPY) return member` — so we
        // just need a stub whose UniqueGuid is anything else. We use
        // DispatchProxy to avoid implementing the 30+ IDecompiler members
        // manually (almost all of which are unreachable from the analyzer).
        public IDecompiler Decompiler => _decompilerStub.Value;
        private static readonly Lazy<IDecompiler> _decompilerStub =
            new(() => DispatchProxy.Create<IDecompiler, NoOpDecompilerProxy>());
    }

    /// <summary>
    /// DispatchProxy implementation that satisfies <see cref="IDecompiler"/>
    /// for the analyzer's <c>GetOriginalCodeLocation</c> path: returns
    /// <see cref="Guid.Empty"/> for <c>UniqueGuid</c> (so the early-return
    /// `UniqueGuid != LANGUAGE_CSHARP_ILSPY` triggers) and a default value
    /// for everything else. Reaching a non-Guid member here means a future
    /// dnSpy.Analyzer.x revision started touching real decompiler features
    /// — we'd see a NullReferenceException downstream and learn we need to
    /// upgrade the stub.
    /// </summary>
    public class NoOpDecompilerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_UniqueGuid" || targetMethod?.Name == "get_GenericGuid")
                return Guid.Empty;
            // Every other property/method on the analyzer code path is
            // unreachable; return the default value of the return type so we
            // don't crash on accidental reads (rather than throwing — which
            // would propagate via DispatchProxy as TargetInvocationException).
            var rt = targetMethod?.ReturnType;
            if (rt == null || rt == typeof(void)) return null;
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }
}
