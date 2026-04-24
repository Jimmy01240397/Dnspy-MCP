using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace DnSpyMcp.Services;

/// <summary>
/// Incremental index of IL-level string literals (ldstr operands) across every
/// assembly currently opened via <see cref="Workspace"/>. Built at open time,
/// pruned on close. Scope is "currently opened assemblies".
///
/// Responsibility is deliberately narrow: only string literal scanning, which
/// neither dnSpy's analyzer DLL nor ICSharpCode.Decompiler ships as a
/// library-accessible service (dnSpy has <c>FilterSearcher</c> but it lives
/// in the main WPF project). Every other cross-DLL query we want —
/// "where-used" xref for methods / types / fields / properties / events —
/// defers to dnSpy's <c>ScopedWhereUsedAnalyzer&lt;T&gt;</c> via
/// Krafs.Publicizer on <c>dnSpy.Analyzer.x.dll</c>, so we don't duplicate
/// the TypeRef-filter + accessibility-scoping machinery dnSpy already has.
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

    private sealed class AsmIndex
    {
        public readonly List<StringHit> StringLiterals = new();
    }

    private readonly ConcurrentDictionary<string, AsmIndex> _perAsm = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Walk a module and add its ldstr instructions to the index.</summary>
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
                }
            }
        }
        _perAsm[asmPath] = idx;
    }

    public void Remove(string asmPath) => _perAsm.TryRemove(asmPath, out _);

    /// <summary>
    /// Enumerate ldstr hits across every currently-indexed assembly (or
    /// restrict to <paramref name="onlyAsmPath"/>). Pagination is the
    /// caller's responsibility.
    /// </summary>
    public IEnumerable<StringHit> FindString(string needle, bool regex, string? onlyAsmPath)
    {
        Regex? rx = null;
        if (regex)
            rx = new Regex(needle, RegexOptions.Compiled | RegexOptions.CultureInvariant);

        IEnumerable<KeyValuePair<string, AsmIndex>> scope = _perAsm;
        if (onlyAsmPath != null)
        {
            var full = Path.GetFullPath(onlyAsmPath);
            if (_perAsm.TryGetValue(full, out var only))
                scope = new[] { new KeyValuePair<string, AsmIndex>(full, only) };
            else
                yield break;
        }

        foreach (var kv in scope)
        {
            foreach (var hit in kv.Value.StringLiterals)
            {
                bool match = rx != null
                    ? rx.IsMatch(hit.Value)
                    : hit.Value.Contains(needle, StringComparison.Ordinal);
                if (match) yield return hit;
            }
        }
    }
}
