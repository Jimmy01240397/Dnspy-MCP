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
    [Description("[FILE] Open a .NET assembly from disk for static analysis (decompile / IL / xref / find). Becomes the active session (subsequent FILE tools may omit asmPath). Path is absolute.")]
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
    [Description("[FILE] Close a previously opened assembly and free its metadata.")]
    public static object AsmClose(Workspace ws, string asmPath)
        => new { closed = ws.Close(asmPath), current = ws.Current };

    [McpServerTool(Name = "reverse_list")]
    [Description("[FILE] List every assembly currently opened in the workspace (multi-session). Marks the active one.")]
    public static object AsmList(Workspace ws)
        => ws.All.Select(a => new {
            path = a.Path,
            name = a.Module.Name?.String,
            types = a.Module.GetTypes().Count(),
            active = string.Equals(a.Path, ws.Current, StringComparison.OrdinalIgnoreCase),
        }).ToArray();

    [McpServerTool(Name = "reverse_current")]
    [Description("[FILE] Return the currently-active asm_file path (used by other FILE tools when asmPath is omitted).")]
    public static object AsmCurrent(Workspace ws) => new { current = ws.Current };

    [McpServerTool(Name = "reverse_switch")]
    [Description("[FILE] Switch the active asm_file session. Subsequent FILE tools can omit asmPath and act on this one.")]
    public static object AsmSwitch(Workspace ws, string asmPath)
    {
        var a = ws.Switch(asmPath);
        return new { current = a.Path };
    }

    [McpServerTool(Name = "reverse_list_types")]
    [Description("[FILE] List types in an opened assembly (paginated). Filters: namePattern (case-insensitive substring on FullName), namespacePattern (exact namespace, or 'Foo.Bar' matches nested 'Foo.Bar.*'). Params: asmPath (optional), namePattern (optional), namespacePattern (optional), offset=0, max=100. Response: {total, offset, returned, truncated, items}.")]
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
    [Description("[FILE] List methods of a type (paginated). Params: typeFullName, asmPath (optional), offset=0, max=200.")]
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
    [Description("[FILE] Decompile a whole type to C# (truncatable — big types blow up context). Params: typeFullName, asmPath (optional), offsetChars=0, maxChars=64000. Response: {totalChars, offsetChars, returnedChars, truncated, text}.")]
    public static object DecompileType(Workspace ws, string typeFullName, string? asmPath = null, int offsetChars = 0, int maxChars = 32_000)
    {
        var a = ws.Get(asmPath);
        var full = a.Decompiler.DecompileTypeAsString(new FullTypeName(typeFullName));
        return Paging.ClampText(full, offsetChars, maxChars);
    }

    [McpServerTool(Name = "reverse_decompile_method")]
    [Description("[FILE] Decompile a single method to C# (truncatable). Params: typeFullName, methodName, asmPath (optional), overloadIndex=0, offsetChars=0, maxChars=64000.")]
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
    [Description("[FILE] Return raw IL for a method (paginated by instruction). Params: typeFullName, methodName, asmPath (optional), overloadIndex=0, offset=0, max=500.")]
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
    [Description("[FILE] Return IL for a method identified by its metadata token (paginated). Params: token, asmPath (optional), offset=0, max=500.")]
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
    [Description("[FILE] Find methods whose IL loads a string literal (ldstr) matching the pattern. Default: case-sensitive substring. Use regex=true to switch to .NET regex (ECMAScript flavor). Always returns the full matched literal in 'value'. Paginated. Params: needle (required), regex=false, asmPath (optional), offset=0, max=100.")]
    public static object FindString(Workspace ws, string needle, bool regex = false, string? asmPath = null, int offset = 0, int max = 100)
    {
        if (string.IsNullOrEmpty(needle)) throw new McpException("needle must be non-empty");
        var a = ws.Get(asmPath);
        Regex? rx = null;
        if (regex)
        {
            try { rx = new Regex(needle, RegexOptions.Compiled | RegexOptions.CultureInvariant); }
            catch (ArgumentException ex) { throw new McpException($"invalid regex '{needle}': {ex.Message}"); }
        }
        var rows = new List<object>();
        foreach (var t in a.Module.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var i in m.Body.Instructions)
                {
                    if (i.OpCode.Code != dnlib.DotNet.Emit.Code.Ldstr || i.Operand is not string s) continue;
                    bool hit = rx != null
                        ? rx.IsMatch(s)
                        : s.Contains(needle, StringComparison.Ordinal);
                    if (hit)
                        rows.Add(new { type = t.FullName, method = m.Name.String, fullName = m.FullName, ilOffset = i.Offset, value = s });
                }
            }
        }
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_xref_to_method")]
    [Description("[FILE] Find all methods that call the given method (paginated). targetFullName accepts full signature ('System.Int32 Ns.Type::Method(System.Int32)') or shorthand 'Ns.Type.Method'. Params: targetFullName, asmPath (optional), offset=0, max=500.")]
    public static object XrefToMethod(Workspace ws, string targetFullName, string? asmPath = null, int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        bool isSignature = targetFullName.Contains("::");
        string? shortDecl = null;
        string? shortName = null;
        if (!isSignature)
        {
            int dot = targetFullName.LastIndexOf('.');
            if (dot > 0)
            {
                shortDecl = targetFullName.Substring(0, dot);
                shortName = targetFullName.Substring(dot + 1);
            }
            else
            {
                shortName = targetFullName;
            }
        }

        var rows = new List<object>();
        foreach (var t in a.Module.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var i in m.Body.Instructions)
                {
                    var op = i.OpCode.Code;
                    if (op != dnlib.DotNet.Emit.Code.Call && op != dnlib.DotNet.Emit.Code.Callvirt && op != dnlib.DotNet.Emit.Code.Newobj) continue;
                    if (i.Operand is not dnlib.DotNet.IMethod target) continue;

                    bool hit = isSignature
                        ? target.FullName == targetFullName
                        : (target.Name.String == shortName &&
                           (shortDecl == null || target.DeclaringType?.FullName == shortDecl));

                    if (hit)
                        rows.Add(new { from = m.FullName, ilOffset = i.Offset, opCode = i.OpCode.Name, target = target.FullName });
                }
            }
        }
        return Paging.Page(rows, offset, max);
    }
}
