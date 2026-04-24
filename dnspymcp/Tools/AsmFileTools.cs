using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DnSpyMcp.Services;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Analyzer.TreeNodes;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DnSpyMcp.Tools;

[McpServerToolType]
public static class AsmFileTools
{
    [McpServerTool(Name = "reverse_open")]
    [Description("[REVERSE] Open a .NET assembly from disk for static analysis (decompile / IL / xref / find). Becomes the active session (subsequent FILE tools may omit asmPath). Path is absolute.")]
    public static object AsmOpen(Workspace ws, string asmPath)
    {
        var a = ws.Open(asmPath);
        return new
        {
            path = a.Path,
            name = a.Module.Name?.String,
            assembly = a.Module.Assembly?.FullName,
            types = a.Module.GetTypes().Count(),
            current = ws.Current,
        };
    }

    [McpServerTool(Name = "reverse_close")]
    [Description("[REVERSE] Close a previously opened assembly and free its metadata.")]
    public static object AsmClose(Workspace ws, string asmPath)
        => new { closed = ws.Close(asmPath), current = ws.Current };

    [McpServerTool(Name = "reverse_list")]
    [Description("[REVERSE] List every assembly currently opened in the workspace (multi-session). Marks the active one.")]
    public static object AsmList(Workspace ws)
        => ws.All.Select(a => new {
            path = a.Path,
            name = a.Module.Name?.String,
            types = a.Module.GetTypes().Count(),
            active = string.Equals(a.Path, ws.Current, StringComparison.OrdinalIgnoreCase),
        }).ToArray();

    [McpServerTool(Name = "reverse_current")]
    [Description("[REVERSE] Return the currently-active asm_file path (used by other FILE tools when asmPath is omitted).")]
    public static object AsmCurrent(Workspace ws) => new { current = ws.Current };

    [McpServerTool(Name = "reverse_switch")]
    [Description("[REVERSE] Switch the active asm_file session. Subsequent FILE tools can omit asmPath and act on this one.")]
    public static object AsmSwitch(Workspace ws, string asmPath)
    {
        var a = ws.Switch(asmPath);
        return new { current = a.Path };
    }

    [McpServerTool(Name = "reverse_list_types")]
    [Description("[REVERSE] List types in an opened assembly (paginated). Filters: namePattern (case-insensitive substring on FullName), namespacePattern (exact namespace, or 'Foo.Bar' matches nested 'Foo.Bar.*'). Params: asmPath (optional), namePattern (optional), namespacePattern (optional), offset=0, max=100. Response: {total, offset, returned, truncated, items}.")]
    public static object ListTypes(Workspace ws, string? asmPath = null, string? namePattern = null, string? namespacePattern = null, int offset = 0, int max = 100)
    {
        var a = ws.Get(asmPath);
        var filtered = a.Module.GetTypes()
            .Where(t => namePattern == null || (t.FullName ?? "").Contains(namePattern, StringComparison.OrdinalIgnoreCase))
            .Where(t =>
            {
                if (namespacePattern == null) return true;
                var ns = t.Namespace?.String ?? "";
                return ns.Equals(namespacePattern, StringComparison.OrdinalIgnoreCase)
                    || ns.StartsWith(namespacePattern + ".", StringComparison.OrdinalIgnoreCase);
            })
            .Select(t => new { fullName = t.FullName, ns = t.Namespace?.String, token = t.MDToken.Raw, methodCount = t.Methods.Count });
        return Paging.Page(filtered, offset, max);
    }

    [McpServerTool(Name = "reverse_list_methods")]
    [Description("[REVERSE] List methods of a type (paginated). Params: typeFullName, asmPath (optional), offset=0, max=200.")]
    public static object ListMethods(Workspace ws, string typeFullName, string? asmPath = null, int offset = 0, int max = 100)
    {
        var a = ws.Get(asmPath);
        var t = a.Module.FindReflection(typeFullName) ?? throw new McpException($"type not found: {typeFullName}");
        var rows = t.Methods.Select(m => new {
            name = m.Name.String,
            fullName = m.FullName,
            token = m.MDToken.Raw,
            hasBody = m.HasBody,
            attributes = m.Attributes.ToString(),
        });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_list_references")]
    [Description("[REVERSE] List the assembly references declared by an opened module's manifest, marking which are currently opened in the workspace and which are still missing. Use this BEFORE reverse_xref_to_method on a target whose callers might live in dependent assemblies — open the missing ones first so the cross-DLL scan actually has them in scope. Each row: {name, version, publicKeyToken (hex), culture, opened (bool), openedAsmPath?}. Params: asmPath (optional, defaults to active session), onlyMissing=false, offset=0, max=100.")]
    public static object ListReferences(Workspace ws, string? asmPath = null, bool onlyMissing = false, int offset = 0, int max = 100)
    {
        var a = ws.Get(asmPath);
        // Pre-build a name->path lookup of what's opened. dnlib AssemblyRef
        // matching is by simple name; version-strict matching is rarely what
        // RE callers want (you usually have one version of mscorlib and want
        // it counted regardless of declared rev).
        var openedByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in ws.All)
        {
            var name = o.Module.Assembly?.Name.String;
            if (!string.IsNullOrEmpty(name) && !openedByName.ContainsKey(name))
                openedByName[name] = o.Path;
        }

        var rows = a.Module.GetAssemblyRefs()
            .Select(r =>
            {
                var name = r.Name.String;
                bool opened = openedByName.TryGetValue(name, out var openPath);
                return new
                {
                    name,
                    version = r.Version?.ToString(),
                    publicKeyToken = FormatPublicKeyToken(r.PublicKeyOrToken),
                    culture = string.IsNullOrEmpty(r.Culture?.String) ? null : r.Culture.String,
                    opened,
                    openedAsmPath = opened ? openPath : null,
                };
            })
            .Where(row => !onlyMissing || !row.opened);
        return Paging.Page(rows, offset, max);
    }

    private static string? FormatPublicKeyToken(PublicKeyBase? pk)
    {
        if (pk is null) return null;
        // PublicKey full form is large — collapse to its 8-byte token for
        // display (matches how AssemblyRef strings appear in IL listings).
        var tok = pk is PublicKey full ? full.Token : pk as PublicKeyToken;
        var bytes = tok?.Data;
        if (bytes is null || bytes.Length == 0) return null;
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    [McpServerTool(Name = "reverse_decompile_type")]
    [Description("[REVERSE] Decompile a whole type to C# (truncatable — big types blow up context). Params: typeFullName, asmPath (optional), offsetChars=0, maxChars=64000. Response: {totalChars, offsetChars, returnedChars, truncated, text}.")]
    public static object DecompileType(Workspace ws, string typeFullName, string? asmPath = null, int offsetChars = 0, int maxChars = 32_000)
    {
        var a = ws.Get(asmPath);
        var full = a.Decompiler.DecompileTypeAsString(new FullTypeName(typeFullName));
        return Paging.ClampText(full, offsetChars, maxChars);
    }

    [McpServerTool(Name = "reverse_decompile_method")]
    [Description("[REVERSE] Decompile a single method to C# (truncatable). Overload selection: pass `signature` (e.g. \"(System.String,System.Int32)\" — lenient, also accepts shorthand like \"(string,int)\"); OR pass `overloadIndex` (zero-based). When neither is given and the method has multiple overloads, the call fails with a list of available signatures. Use `reverse_list_overloads` to enumerate. Params: typeFullName, methodName, asmPath (optional), signature (optional), overloadIndex (optional), offsetChars=0, maxChars=64000.")]
    public static object DecompileMethod(Workspace ws, string typeFullName, string methodName,
                                         string? asmPath = null, string? signature = null, int? overloadIndex = null,
                                         int offsetChars = 0, int maxChars = 64_000)
    {
        var a = ws.Get(asmPath);
        var m = ResolveOverload(a.Module, typeFullName, methodName, signature, overloadIndex);
        var handle = MetadataTokens.MethodDefinitionHandle((int)m.MDToken.Rid);
        var full = a.Decompiler.DecompileAsString(handle);
        return Paging.ClampText(full, offsetChars, maxChars);
    }

    [McpServerTool(Name = "reverse_il_method")]
    [Description("[REVERSE] Return raw IL for a method (paginated by instruction). Overload selection: see reverse_decompile_method. Params: typeFullName, methodName, asmPath (optional), signature (optional), overloadIndex (optional), offset=0, max=500.")]
    public static object IlMethod(Workspace ws, string typeFullName, string methodName,
                                  string? asmPath = null, string? signature = null, int? overloadIndex = null,
                                  int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var m = ResolveOverload(a.Module, typeFullName, methodName, signature, overloadIndex);
        if (!m.HasBody) return new { total = 0, offset, returned = 0, truncated = false, items = Array.Empty<object>() };
        var rows = m.Body.Instructions.Select(i => new { offset = i.Offset, opCode = i.OpCode.Name, operand = i.Operand?.ToString() });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_list_overloads")]
    [Description("[REVERSE] Enumerate every overload of methodName on typeFullName. Each row gives the index, full signature, parameter list, return type, and metadata token — feed them back as `overloadIndex` or `signature` to reverse_decompile_method / reverse_il_method. Params: typeFullName, methodName, asmPath (optional), offset=0, max=200.")]
    public static object ListOverloads(Workspace ws, string typeFullName, string methodName,
                                       string? asmPath = null, int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var t = a.Module.FindReflection(typeFullName) ?? throw new McpException($"type not found: {typeFullName}");
        var overloads = t.Methods.Where(mm => mm.Name == methodName).ToList();
        if (overloads.Count == 0) throw new McpException($"method not found: {typeFullName}::{methodName}");
        var rows = overloads.Select((m, i) => new
        {
            index = i,
            fullName = m.FullName,
            signature = ParamSignature(m),
            parameters = m.Parameters.Where(p => p.IsNormalMethodParameter).Select(p => new { name = p.Name, type = p.Type?.FullName }).ToArray(),
            returnType = m.ReturnType?.FullName,
            token = m.MDToken.Raw,
            isStatic = m.IsStatic,
        });
        return Paging.Page(rows, offset, max);
    }

    // Resolve a method by name + optional signature/overloadIndex. Signature
    // matching is lenient: full "(System.String,System.Int32)" wins, but
    // shorthand like "(string,int)" also matches by basename. When ambiguous
    // and no selector given, the error lists every available signature so the
    // caller can re-issue with overloadIndex or signature.
    private static MethodDef ResolveOverload(ModuleDef module, string typeFullName, string methodName,
                                             string? signature, int? overloadIndex)
    {
        var t = module.FindReflection(typeFullName) ?? throw new McpException($"type not found: {typeFullName}");
        var overloads = t.Methods.Where(m => m.Name == methodName).ToList();
        if (overloads.Count == 0)
            throw new McpException($"method not found: {typeFullName}::{methodName}");

        if (overloadIndex.HasValue)
        {
            if (signature != null)
                throw new McpException("pass either signature or overloadIndex, not both");
            int idx = overloadIndex.Value;
            if (idx < 0 || idx >= overloads.Count)
                throw new McpException($"overloadIndex out of range (have {overloads.Count})");
            return overloads[idx];
        }

        if (signature != null)
        {
            var matches = overloads.Where(m => SignatureMatches(m, signature)).ToList();
            if (matches.Count == 0)
                throw AmbiguityError(typeFullName, methodName, overloads, $"signature '{signature}' did not match any overload");
            if (matches.Count > 1)
                throw AmbiguityError(typeFullName, methodName, matches, $"signature '{signature}' matched {matches.Count} overloads (be more specific)");
            return matches[0];
        }

        if (overloads.Count == 1) return overloads[0];
        throw AmbiguityError(typeFullName, methodName, overloads, $"{overloads.Count} overloads — pass `signature` or `overloadIndex`");
    }

    private static McpException AmbiguityError(string typeFullName, string methodName, IList<MethodDef> overloads, string reason)
    {
        var lines = overloads.Select((m, i) => $"  [{i}] {ParamSignature(m)}");
        return new McpException(
            $"{typeFullName}::{methodName} — {reason}.\nAvailable:\n" + string.Join("\n", lines));
    }

    private static bool SignatureMatches(MethodDef m, string signature)
    {
        var sig = NormalizeParams(signature);
        var have = ParamList(m);
        if (sig.SequenceEqual(have, StringComparer.Ordinal)) return true;
        // Lenient: compare by short name only (last "." segment, strip generic
        // arity). Lets callers pass "string" / "int" instead of fully qualified.
        var shortHave = have.Select(ShortTypeName).ToArray();
        var shortSig = sig.Select(ShortTypeName).ToArray();
        return shortSig.SequenceEqual(shortHave, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] ParamList(MethodDef m)
        => m.Parameters.Where(p => p.IsNormalMethodParameter)
            .Select(p => p.Type?.FullName ?? "")
            .ToArray();

    private static string ParamSignature(MethodDef m)
    {
        var ret = m.ReturnType?.FullName ?? "void";
        var ps = string.Join(",", ParamList(m));
        return $"{ret} {m.Name}({ps})";
    }

    private static string[] NormalizeParams(string signature)
    {
        var s = signature.Trim();
        // Accept "(a,b)" or "a,b". Strip outer parens.
        if (s.StartsWith("(") && s.EndsWith(")")) s = s[1..^1];
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        return s.Split(',').Select(p => p.Trim()).ToArray();
    }

    private static string ShortTypeName(string fullName)
    {
        var s = fullName;
        // strip backtick generic arity
        var tick = s.IndexOf('`');
        if (tick >= 0) s = s[..tick];
        var dot = s.LastIndexOf('.');
        if (dot >= 0) s = s[(dot + 1)..];
        // canonical aliases (System.String -> String -> string)
        return s switch
        {
            "String" => "string",
            "Int32" => "int",
            "Int64" => "long",
            "UInt32" => "uint",
            "UInt64" => "ulong",
            "Int16" => "short",
            "UInt16" => "ushort",
            "Byte" => "byte",
            "SByte" => "sbyte",
            "Boolean" => "bool",
            "Char" => "char",
            "Single" => "float",
            "Double" => "double",
            "Decimal" => "decimal",
            "Object" => "object",
            "Void" => "void",
            _ => s.ToLowerInvariant(),
        };
    }

    [McpServerTool(Name = "reverse_il_method_by_token")]
    [Description("[REVERSE] Return IL for a method identified by its metadata token (paginated). Params: token, asmPath (optional), offset=0, max=500.")]
    public static object IlByToken(Workspace ws, uint token, string? asmPath = null, int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var md = a.Module.ResolveToken(token) as MethodDef
            ?? throw new McpException($"method not found for token 0x{token:X8}");
        if (!md.HasBody) return new { total = 0, offset, returned = 0, truncated = false, items = Array.Empty<object>() };
        var rows = md.Body.Instructions.Select(i => new { offset = i.Offset, opCode = i.OpCode.Name, operand = i.Operand?.ToString() });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_find_string")]
    [Description("[REVERSE] Find methods whose IL loads a string literal (ldstr) matching the pattern. Default scope: EVERY currently-opened assembly (cross-DLL). Pass asmPath to limit to a single one. Matching is case-sensitive substring by default; pass regex=true for .NET regex. Always returns the full matched literal in 'value' plus the assembly it lives in. Paginated. Params: needle (required), regex=false, asmPath (optional, defaults to all-opened), offset=0, max=100.")]
    public static object FindString(Workspace ws, string needle, bool regex = false, string? asmPath = null, int offset = 0, int max = 100)
    {
        if (string.IsNullOrEmpty(needle)) throw new McpException("needle must be non-empty");
        // asmPath semantics: null / empty = cross-DLL (all opened); otherwise
        // limited to that one. We validate the limit path exists so a typo
        // doesn't silently fall through to "found nothing".
        string? scope = null;
        if (!string.IsNullOrEmpty(asmPath))
            scope = ws.Get(asmPath).Path;
        // Compile regex here so the caller gets a descriptive error rather
        // than an opaque RegexParseException from the enumerator.
        if (regex)
        {
            try { _ = new Regex(needle, RegexOptions.Compiled); }
            catch (ArgumentException ex) { throw new McpException($"invalid regex '{needle}': {ex.Message}"); }
        }
        var rows = ws.Index.FindString(needle, regex, scope)
            .Select(h => new { asm = h.AsmPath, type = h.TypeFullName, method = h.MethodName, fullName = h.MethodFullName, ilOffset = h.IlOffset, value = h.Value });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_xref_to_method")]
    [Description("[REVERSE] Find all methods that call the given method using dnSpy's ScopedWhereUsedAnalyzer engine (cross-DLL, accessibility-aware: Private/Internal/Public scoping, TypeRef pre-filter, friend-assembly handling, type-equivalence). Scope: every currently-opened assembly. targetFullName accepts full signature ('System.Int32 Ns.Type::Method(System.Int32)') OR shorthand 'Ns.Type.Method' (matches any overload, AND any per-module instance — useful when the same DLL is opened from multiple paths). Response rows include the assembly each caller lives in. Paginated. Params: targetFullName (required), asmPath (optional — if given, only resolve the target method definition within this one asm; scope of caller search still spans all opened asms), offset=0, max=200.")]
    public static object XrefToMethod(Workspace ws, string targetFullName, string? asmPath = null, int offset = 0, int max = 200)
    {
        if (string.IsNullOrEmpty(targetFullName))
            throw new McpException("targetFullName must be non-empty");

        // Resolve EVERY MethodDef matching the input — shorthand may match
        // multiple overloads (or multiple per-module instances of the same
        // logical method when the same asm is opened twice). Each is fed to
        // dnSpy's analyzer; we dedupe callers across analyses.
        var targets = ResolveMethods(ws, targetFullName, asmPath).ToList();
        if (targets.Count == 0)
            throw new McpException($"method not found: {targetFullName}");

        // Drive dnSpy's engine for each resolved target. The engine handles
        // accessibility scoping / TypeRef pre-filtering / friend assemblies /
        // parallel module traversal — we only supply the per-type callback.
        var docService = new WorkspaceDocumentService(ws);
        var hits = new List<object>();
        var seen = new HashSet<MethodDef>(MethodDefComparer.Instance);
        foreach (var targetMethod in targets)
        {
            var analyzer = new ScopedWhereUsedAnalyzer<(MethodDef caller, Instruction instr)>(
                docService, targetMethod,
                type => FindCallersInType(type, targetMethod));
            foreach (var (caller, instr) in analyzer.PerformAnalysis(CancellationToken.None))
            {
                if (!seen.Add(caller)) continue;
                var asmPathOut = caller.Module?.Location ?? "";
                hits.Add(new { asm = asmPathOut, from = caller.FullName, ilOffset = instr.Offset, opCode = instr.OpCode.Name, target = targetMethod.FullName });
            }
        }
        return Paging.Page(hits, offset, max);
    }

    // Per-type callback for ScopedWhereUsedAnalyzer: find call/callvirt/newobj
    // to the target method. Uses dnSpy.Analyzer.Helpers + SigComparer-style
    // logic, exposed via Publicizer. Not duplicated: every analyzer in dnSpy
    // writes its own per-type callback (MethodUsedBy, TypeUsedBy, FieldAccess
    // each have different ones) because THIS is the per-query part.
    private static IEnumerable<(MethodDef caller, Instruction instr)> FindCallersInType(TypeDef type, MethodDef target)
    {
        string targetName = target.Name;
        foreach (var method in type.Methods)
        {
            if (!method.HasBody) continue;
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.Operand is dnlib.DotNet.IMethod mr && !mr.IsField && mr.Name == targetName &&
                    Helpers.IsReferencedBy(target.DeclaringType, mr.DeclaringType) &&
                    AreSameMethod(mr.ResolveMethodDef(), target))
                {
                    yield return (method, instr);
                    break;  // one hit per caller is enough
                }
            }
        }
    }

    private static bool AreSameMethod(MethodDef? a, MethodDef? b)
    {
        if (a is null || b is null) return false;
        return a == b || new SigComparer().Equals(a, b);
    }

    // Look up methods matching either a full signature ('T Ns.Type::M(args)')
    // or shorthand ('Ns.Type.M' / 'M'). Yields every match — the caller
    // (xref) feeds each to the analyzer separately, so multiple per-module
    // instances or multiple overloads under the same shorthand all get
    // analyzed and the result-set is unioned + deduped.
    private static IEnumerable<MethodDef> ResolveMethods(Workspace ws, string target, string? onlyAsmPath)
    {
        bool isSignature = target.Contains("::");
        string? declTypeName = null;
        string? methodName = null;
        if (!isSignature)
        {
            int dot = target.LastIndexOf('.');
            if (dot > 0)
            {
                declTypeName = target.Substring(0, dot);
                methodName = target.Substring(dot + 1);
            }
            else
            {
                methodName = target;
            }
        }

        IEnumerable<Workspace.OpenedAsm> scope = ws.All;
        if (!string.IsNullOrEmpty(onlyAsmPath))
            scope = new[] { ws.Get(onlyAsmPath) };

        foreach (var asm in scope)
        {
            foreach (var t in asm.Module.GetTypes())
            {
                if (declTypeName != null && t.FullName != declTypeName) continue;
                foreach (var m in t.Methods)
                {
                    if (isSignature ? m.FullName == target : m.Name == methodName)
                        yield return m;
                }
            }
        }
    }

    private sealed class MethodDefComparer : IEqualityComparer<MethodDef>
    {
        public static readonly MethodDefComparer Instance = new();
        public bool Equals(MethodDef? x, MethodDef? y) => ReferenceEquals(x, y);
        public int GetHashCode(MethodDef obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
