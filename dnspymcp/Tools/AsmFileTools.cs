using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;
using DnSpyMcp.Services;
using dnlib.DotNet;
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

    [McpServerTool(Name = "reverse_decompile_type")]
    [Description("[REVERSE] Decompile a whole type to C# (truncatable — big types blow up context). Params: typeFullName, asmPath (optional), offsetChars=0, maxChars=64000. Response: {totalChars, offsetChars, returnedChars, truncated, text}.")]
    public static object DecompileType(Workspace ws, string typeFullName, string? asmPath = null, int offsetChars = 0, int maxChars = 32_000)
    {
        var a = ws.Get(asmPath);
        var full = a.Decompiler.DecompileTypeAsString(new FullTypeName(typeFullName));
        return Paging.ClampText(full, offsetChars, maxChars);
    }

    [McpServerTool(Name = "reverse_decompile_method")]
    [Description("[REVERSE] Decompile a single method to C# (truncatable). Params: typeFullName, methodName, asmPath (optional), overloadIndex=0, offsetChars=0, maxChars=64000.")]
    public static object DecompileMethod(Workspace ws, string typeFullName, string methodName,
                                         string? asmPath = null, int overloadIndex = 0,
                                         int offsetChars = 0, int maxChars = 64_000)
    {
        var a = ws.Get(asmPath);
        var t = a.Module.FindReflection(typeFullName) ?? throw new McpException($"type not found: {typeFullName}");
        var overloads = t.Methods.Where(m => m.Name == methodName).ToList();
        if (overloads.Count == 0) throw new McpException($"method not found: {typeFullName}::{methodName}");
        if (overloadIndex < 0 || overloadIndex >= overloads.Count) throw new McpException($"overloadIndex out of range (have {overloads.Count})");
        var m = overloads[overloadIndex];
        var handle = MetadataTokens.MethodDefinitionHandle((int)m.MDToken.Rid);
        var full = a.Decompiler.DecompileAsString(handle);
        return Paging.ClampText(full, offsetChars, maxChars);
    }

    [McpServerTool(Name = "reverse_il_method")]
    [Description("[REVERSE] Return raw IL for a method (paginated by instruction). Params: typeFullName, methodName, asmPath (optional), overloadIndex=0, offset=0, max=500.")]
    public static object IlMethod(Workspace ws, string typeFullName, string methodName,
                                  string? asmPath = null, int overloadIndex = 0,
                                  int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var t = a.Module.FindReflection(typeFullName) ?? throw new McpException($"type not found: {typeFullName}");
        var overloads = t.Methods.Where(m => m.Name == methodName).ToList();
        if (overloads.Count == 0) throw new McpException($"method not found: {typeFullName}::{methodName}");
        if (overloadIndex < 0 || overloadIndex >= overloads.Count) throw new McpException($"overloadIndex out of range");
        var m = overloads[overloadIndex];
        if (!m.HasBody) return new { total = 0, offset, returned = 0, truncated = false, items = Array.Empty<object>() };
        var rows = m.Body.Instructions.Select(i => new { offset = i.Offset, opCode = i.OpCode.Name, operand = i.Operand?.ToString() });
        return Paging.Page(rows, offset, max);
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
    [Description("[REVERSE] Find all methods that call the given method. Default scope: EVERY currently-opened assembly (cross-DLL). Pass asmPath to limit. targetFullName accepts full signature ('System.Int32 Ns.Type::Method(System.Int32)') OR shorthand 'Ns.Type.Method' (matches any overload). Response rows include the assembly each caller lives in. Paginated. Params: targetFullName, asmPath (optional, defaults to all-opened), offset=0, max=200.")]
    public static object XrefToMethod(Workspace ws, string targetFullName, string? asmPath = null, int offset = 0, int max = 200)
    {
        if (string.IsNullOrEmpty(targetFullName))
            throw new McpException("targetFullName must be non-empty");
        string? scope = null;
        if (!string.IsNullOrEmpty(asmPath))
            scope = ws.Get(asmPath).Path;
        var rows = ws.Index.XrefToMethod(targetFullName, scope)
            .Select(s => new { asm = s.AsmPath, from = s.FromMethodFullName, ilOffset = s.IlOffset, opCode = s.OpCode, target = s.TargetFullName });
        return Paging.Page(rows, offset, max);
    }
}
