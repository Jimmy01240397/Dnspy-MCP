using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace DnSpyMcp.Services;

/// <summary>
/// Workspace-wide (cross-DLL) metadata and IL index. Built incrementally as
/// assemblies are opened / closed through <see cref="Workspace"/>.
///
/// The index is the thing that lets <c>reverse_find_string</c> /
/// <c>reverse_xref_to_method</c> default to "search every currently-opened
/// assembly" without us having to dnlib-walk every module on every query.
/// Costs moved to open-time, paid once; the old find_string had to walk 1400
/// DLL bodies per query — that was the single worst wart in the v1 surface.
///
/// Scope is deliberately "currently opened assemblies". The caller is the
/// one who chooses which DLLs are in scope via <c>reverse_open</c>; this
/// class does not auto-scan a directory. If you want whole-GAC coverage,
/// open every GAC DLL up-front.
///
/// Implementation note — why we hand-walk instructions instead of calling
/// "a library's xref API":
///   - ILSpy has an "Analyze Method" feature that finds callers, but it
///     lives in the WPF UI project (ILSpy/Analyzers/Builtin/*) and isn't a
///     public API of ICSharpCode.Decompiler.
///   - dnSpy ships the same feature via dnSpy.Analyzer, also WPF-coupled.
///   - dnlib (the metadata layer we use) has no built-in xref.
///   - Under the hood every one of these implementations does exactly what
///     we do here: walk every method body's IL instructions and match on
///     the call/callvirt/newobj operand.
/// So "手刻" here isn't an alternative to a library shortcut; it IS the
/// library pattern. The CCXref scanner (tools/clientcallable_scan/) under
/// the SharePoint research tree is the same shape.
///
/// Known limitation: matching is on FullName string equality. Generic
/// method signatures with !!0 / !!T parameters are represented by dnlib's
/// FullName in a way that may not match the raw target string the caller
/// types. If this becomes a blocker, migrate the index to
/// ICSharpCode.Decompiler's TypeSystem (which resolves MethodSpec /
/// MethodDef across assemblies properly) instead of dnlib.IMethod.FullName.
/// </summary>
internal sealed class CrossDllIndex
{
    public sealed record StringHit(
        string AsmPath,
        string TypeFullName,
        string MethodName,
        string MethodFullName,
        uint IlOffset,
        string Value);

    public sealed record CallSite(
        string AsmPath,
        string FromMethodFullName,
        uint IlOffset,
        string OpCode,
        string TargetFullName);

    /// <summary>Everything we've pulled out of a single module body walk.</summary>
    private sealed class AsmIndex
    {
        // Flat list of ldstr hits. find_string is O(n) anyway (substring /
        // regex over the literal) so we don't bother bucketing.
        public readonly List<StringHit> StringLiterals = new();
        // Callee-keyed call graph. The key is the callee's full signature
        // ("System.Int32 Foo.Bar::Baz(System.String)") OR its shorthand
        // ("Foo.Bar.Baz") — we index both so xref queries can use either.
        // Value is the list of caller sites.
        public readonly Dictionary<string, List<CallSite>> BySignature = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<CallSite>> ByShorthand = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, AsmIndex> _perAsm = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Walk a module and add its ldstr + call/newobj instructions to the index.</summary>
    public void Add(string asmPath, ModuleDefMD module)
    {
        var idx = new AsmIndex();
        foreach (var t in module.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode.Code == Code.Ldstr && ins.Operand is string s)
                    {
                        idx.StringLiterals.Add(new StringHit(
                            asmPath, t.FullName, m.Name.String, m.FullName, ins.Offset, s));
                    }
                    else if ((ins.OpCode.Code == Code.Call ||
                              ins.OpCode.Code == Code.Callvirt ||
                              ins.OpCode.Code == Code.Newobj) &&
                             ins.Operand is IMethod target)
                    {
                        var site = new CallSite(
                            asmPath, m.FullName, ins.Offset, ins.OpCode.Name, target.FullName);
                        Bucket(idx.BySignature, target.FullName, site);
                        var shorthand = TargetShorthand(target);
                        if (shorthand != null)
                            Bucket(idx.ByShorthand, shorthand, site);
                    }
                }
            }
        }
        _perAsm[asmPath] = idx;
    }

    public void Remove(string asmPath) => _perAsm.TryRemove(asmPath, out _);

    public IReadOnlyCollection<string> AllAsmPaths => (IReadOnlyCollection<string>)_perAsm.Keys;

    // ---------- query ----------------------------------------------------

    /// <summary>
    /// Enumerate ldstr hits across every currently-indexed assembly (or
    /// restrict to <paramref name="onlyAsmPath"/> if non-null). Pagination is
    /// the caller's responsibility — we return the raw stream so the tool
    /// layer can wrap it in the standard envelope.
    /// </summary>
    public IEnumerable<StringHit> FindString(string needle, bool regex, string? onlyAsmPath)
    {
        Regex? rx = null;
        if (regex)
        {
            // Caller should have validated already; throw here matches the
            // in-place behavior so invalid regex never yields zero rows silently.
            rx = new Regex(needle, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }

        foreach (var (asmPath, idx) in AsmScope(onlyAsmPath))
        {
            foreach (var hit in idx.StringLiterals)
            {
                bool match = rx != null
                    ? rx.IsMatch(hit.Value)
                    : hit.Value.Contains(needle, StringComparison.Ordinal);
                if (match) yield return hit;
            }
        }
    }

    /// <summary>
    /// Enumerate all call-sites whose callee matches <paramref name="targetFullName"/>.
    /// Accepts either a full signature ('T Ns.Type::M(args)') or shorthand
    /// ('Ns.Type.M' or just 'M'). Shorthand matches any overload of that name.
    /// </summary>
    public IEnumerable<CallSite> XrefToMethod(string targetFullName, string? onlyAsmPath)
    {
        bool isSignature = targetFullName.Contains("::");
        foreach (var (_, idx) in AsmScope(onlyAsmPath))
        {
            if (isSignature)
            {
                if (idx.BySignature.TryGetValue(targetFullName, out var list))
                    foreach (var s in list) yield return s;
            }
            else
            {
                if (idx.ByShorthand.TryGetValue(targetFullName, out var list))
                    foreach (var s in list) yield return s;
            }
        }
    }

    // ---------- plumbing --------------------------------------------------

    private IEnumerable<(string AsmPath, AsmIndex Idx)> AsmScope(string? onlyAsmPath)
    {
        if (onlyAsmPath != null)
        {
            var full = Path.GetFullPath(onlyAsmPath);
            if (_perAsm.TryGetValue(full, out var idx))
                yield return (full, idx);
            yield break;
        }
        foreach (var kv in _perAsm)
            yield return (kv.Key, kv.Value);
    }

    private static void Bucket<T>(Dictionary<string, List<T>> d, string key, T value)
    {
        if (!d.TryGetValue(key, out var list))
            d[key] = list = new List<T>();
        list.Add(value);
    }

    /// <summary>
    /// Derive an xref-friendly shorthand for a callee: "Namespace.Type.Method".
    /// We don't encode arguments here so callers can search by name regardless
    /// of overload; exact-overload resolution is what the full signature form
    /// is for.
    /// </summary>
    private static string? TargetShorthand(IMethod target)
    {
        var declType = target.DeclaringType?.FullName;
        var name = target.Name?.String;
        if (string.IsNullOrEmpty(name)) return null;
        return string.IsNullOrEmpty(declType) ? name : $"{declType}.{name}";
    }
}
