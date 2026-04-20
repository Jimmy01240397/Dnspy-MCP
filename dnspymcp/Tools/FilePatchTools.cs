using System.ComponentModel;
using DnSpyMcp.Services;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DnSpyMcp.Tools;

/// <summary>
/// Static / on-disk patch tools. These modify the *file*, not a running process.
/// For live memory edits, use live_memory_write instead.
/// </summary>
[McpServerToolType]
public static class FilePatchTools
{
    [McpServerTool(Name = "file_patch_il_nop")]
    [Description("[FILE] Replace a range of IL instructions with nops and save to outputPath. Params: typeFullName, methodName, startOffset, endOffset (inclusive), outputPath, asmPath (optional), overloadIndex=0.")]
    public static object PatchIlNop(Workspace ws, string typeFullName, string methodName,
                                    int startOffset, int endOffset, string outputPath, string? asmPath = null, int overloadIndex = 0)
    {
        var a = ws.Get(asmPath);
        var t = a.Module.FindReflection(typeFullName) ?? throw new McpException($"type not found: {typeFullName}");
        var overloads = t.Methods.Where(m => m.Name == methodName).ToList();
        if (overloads.Count == 0) throw new McpException($"method not found: {typeFullName}::{methodName}");
        if (overloadIndex < 0 || overloadIndex >= overloads.Count) throw new McpException($"overloadIndex out of range (have {overloads.Count})");
        var m = overloads[overloadIndex];
        if (!m.HasBody) throw new McpException("method has no body");
        int changed = 0;
        foreach (var instr in m.Body.Instructions)
        {
            if (instr.Offset >= startOffset && instr.Offset <= endOffset)
            {
                instr.OpCode = OpCodes.Nop;
                instr.Operand = null;
                changed++;
            }
        }
        a.Module.Write(outputPath);
        return new { changedInstructions = changed, written = outputPath };
    }

    [McpServerTool(Name = "file_patch_bytes")]
    [Description("[FILE] Overwrite raw file bytes at a given file offset. Params: filePath (any binary), offset:long, hex:string. Returns {written}.")]
    public static object PatchBytes(string filePath, long offset, string hex)
    {
        hex = hex.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        if ((hex.Length & 1) != 0) throw new McpException("hex must be even length");
        var data = new byte[hex.Length / 2];
        for (int i = 0; i < data.Length; i++)
            data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return new { written = data.Length, filePath, offset };
    }

    [McpServerTool(Name = "file_save_assembly")]
    [Description("[FILE] Write the in-memory ModuleDef (with your patches) back to a new path. Params: outputPath, asmPath (optional).")]
    public static object SaveAssembly(Workspace ws, string outputPath, string? asmPath = null)
    {
        var a = ws.Get(asmPath);
        a.Module.Write(outputPath);
        return new { written = outputPath };
    }
}
