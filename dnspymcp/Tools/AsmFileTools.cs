using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using DnSpyMcp.Services;
using dnlib.DotNet;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol.Server;

namespace DnSpyMcp.Tools;

[McpServerToolType]
public static class AsmFileTools
{
    [McpServerTool(Name = "asm_file_open")]
    [Description("[FILE] Open a .NET assembly from disk for static analysis (decompile / IL / xref / find). The asmPath is used as the handle for all subsequent FILE tools. Path is absolute.")]
    public static object AsmOpen(Workspace ws, string asmPath)
    {
        var a = ws.Open(asmPath);
        return new
        {
            path = a.Path,
            name = a.Module.Name?.String,
            assembly = a.Module.Assembly?.FullName,
            types = a.Module.GetTypes().Count(),
        };
    }

    [McpServerTool(Name = "asm_file_close")]
    [Description("[FILE] Close a previously opened assembly and free its metadata.")]
    public static object AsmClose(Workspace ws, string asmPath)
        => new { closed = ws.Close(asmPath) };

    [McpServerTool(Name = "asm_file_list")]
    [Description("[FILE] List the assemblies currently opened in the workspace (paths and type counts).")]
    public static object AsmList(Workspace ws)
        => ws.All.Select(a => new { path = a.Path, name = a.Module.Name?.String, types = a.Module.GetTypes().Count() }).ToArray();

    [McpServerTool(Name = "asm_file_list_types")]
    [Description("[FILE] List all types defined in the opened assembly. Params: asmPath, namePattern (optional substring filter).")]
    public static object ListTypes(Workspace ws, string asmPath, string? namePattern = null)
    {
        var a = ws.Get(asmPath);
        return a.Module.GetTypes()
            .Where(t => namePattern == null || (t.FullName ?? "").Contains(namePattern, StringComparison.OrdinalIgnoreCase))
            .Select(t => new { fullName = t.FullName, token = t.MDToken.Raw, methodCount = t.Methods.Count })
            .ToArray();
    }

    [McpServerTool(Name = "asm_file_list_methods")]
    [Description("[FILE] List methods of a type. Params: asmPath, typeFullName.")]
    public static object ListMethods(Workspace ws, string asmPath, string typeFullName)
    {
        var a = ws.Get(asmPath);
        var t = a.Module.FindReflection(typeFullName) ?? throw new ArgumentException($"type not found: {typeFullName}");
        return t.Methods.Select(m => new {
            name = m.Name.String,
            fullName = m.FullName,
            token = m.MDToken.Raw,
            hasBody = m.HasBody,
            attributes = m.Attributes.ToString(),
        }).ToArray();
    }

    [McpServerTool(Name = "decompile_type")]
    [Description("[FILE] Decompile a whole type to C#. Params: asmPath, typeFullName.")]
    public static string DecompileType(Workspace ws, string asmPath, string typeFullName)
    {
        var a = ws.Get(asmPath);
        return a.Decompiler.DecompileTypeAsString(new FullTypeName(typeFullName));
    }

    [McpServerTool(Name = "decompile_method")]
    [Description("[FILE] Decompile a single method to C#. Params: asmPath, typeFullName, methodName, overloadIndex=0.")]
    public static string DecompileMethod(Workspace ws, string asmPath, string typeFullName, string methodName, int overloadIndex = 0)
    {
        var a = ws.Get(asmPath);
        var t = a.Module.FindReflection(typeFullName) ?? throw new ArgumentException($"type not found: {typeFullName}");
        var overloads = t.Methods.Where(m => m.Name == methodName).ToList();
        if (overloads.Count == 0) throw new ArgumentException($"method not found: {typeFullName}::{methodName}");
        if (overloadIndex < 0 || overloadIndex >= overloads.Count) throw new ArgumentException($"overloadIndex out of range (have {overloads.Count})");
        var m = overloads[overloadIndex];
        var handle = MetadataTokens.MethodDefinitionHandle((int)m.MDToken.Rid);
        return a.Decompiler.DecompileAsString(handle);
    }

    [McpServerTool(Name = "il_method")]
    [Description("[FILE] Return raw IL for a method (mnemonic + operand). Params: asmPath, typeFullName, methodName, overloadIndex=0.")]
    public static string IlMethod(Workspace ws, string asmPath, string typeFullName, string methodName, int overloadIndex = 0)
    {
        var a = ws.Get(asmPath);
        var t = a.Module.FindReflection(typeFullName) ?? throw new ArgumentException($"type not found: {typeFullName}");
        var overloads = t.Methods.Where(m => m.Name == methodName).ToList();
        if (overloadIndex < 0 || overloadIndex >= overloads.Count) throw new ArgumentException($"overloadIndex out of range");
        var m = overloads[overloadIndex];
        if (!m.HasBody) return "<no body>";
        var sb = new StringBuilder();
        foreach (var instr in m.Body.Instructions)
            sb.AppendLine($"IL_{instr.Offset:X4}: {instr.OpCode.Name,-12} {instr.Operand}");
        return sb.ToString();
    }

    [McpServerTool(Name = "il_method_by_token")]
    [Description("[FILE] Return IL for a method identified by its metadata token. Params: asmPath, token.")]
    public static string IlByToken(Workspace ws, string asmPath, uint token)
    {
        var a = ws.Get(asmPath);
        var md = a.Module.ResolveToken(token) as MethodDef
            ?? throw new ArgumentException($"method not found for token 0x{token:X8}");
        if (!md.HasBody) return "<no body>";
        var sb = new StringBuilder();
        foreach (var i in md.Body.Instructions)
            sb.AppendLine($"IL_{i.Offset:X4}: {i.OpCode.Name,-12} {i.Operand}");
        return sb.ToString();
    }

    [McpServerTool(Name = "find_string")]
    [Description("[FILE] Find all methods whose IL body contains a string literal matching the given substring. Params: asmPath, needle, max=200.")]
    public static object FindString(Workspace ws, string asmPath, string needle, int max = 200)
    {
        var a = ws.Get(asmPath);
        var rows = new List<object>();
        foreach (var t in a.Module.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var i in m.Body.Instructions)
                {
                    if (i.OpCode.Code == dnlib.DotNet.Emit.Code.Ldstr &&
                        i.Operand is string s &&
                        s.Contains(needle, StringComparison.Ordinal))
                    {
                        rows.Add(new { type = t.FullName, method = m.Name.String, fullName = m.FullName, ilOffset = i.Offset, value = s });
                        if (rows.Count >= max) return rows.ToArray();
                    }
                }
            }
        }
        return rows.ToArray();
    }

    [McpServerTool(Name = "xref_to_method")]
    [Description("[FILE] Find all methods that call the given method (full name match). Params: asmPath, targetFullName, max=500.")]
    public static object XrefToMethod(Workspace ws, string asmPath, string targetFullName, int max = 500)
    {
        var a = ws.Get(asmPath);
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
                    if (i.Operand is dnlib.DotNet.IMethod target && target.FullName == targetFullName)
                    {
                        rows.Add(new { from = m.FullName, ilOffset = i.Offset, opCode = i.OpCode.Name });
                        if (rows.Count >= max) return rows.ToArray();
                    }
                }
            }
        }
        return rows.ToArray();
    }
}
