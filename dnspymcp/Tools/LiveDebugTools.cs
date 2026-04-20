using System.ComponentModel;
using DnSpyMcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DnSpyMcp.Tools;

/// <summary>
/// Thin proxies over the dnspymcpagent TCP+NDJSON backend. Every tool here
/// is [LIVE] — it talks to a running / dumped .NET process through an
/// <see cref="AgentClient"/>. Multiple agents can be connected at once (one
/// per named slot in <see cref="AgentRegistry"/>); tools default to the
/// active slot, or you can pass <c>agent</c> to target a specific one.
/// </summary>
[McpServerToolType]
public static class LiveDebugTools
{
    // ---- agent session management (idalib-style) ----------------------
    // Each agent endpoint (host:port) is a named session. Open once, then
    // list / switch between them for free — the TCP connection is kept warm
    // and auto-reconnects if the underlying agent restarts. You never need
    // to disconnect+reconnect between tool calls; just `switch`.

    [McpServerTool(Name = "live_agent_open")]
    [Description("[LIVE] Open (or re-open) a named session to a dnspymcpagent at host:port and make it active. Idempotent — calling with an existing name reconfigures and reconnects that slot. Params: host (required), port (required), token=null, name='default'.")]
    public static object AgentOpen(AgentRegistry reg, string host, int port, string? token = null, string name = "default")
    {
        var agent = reg.GetOrCreate(name);
        agent.Configure(host, port, token);
        agent.Connect();
        reg.SetActive(name);
        return new { opened = true, name, host, port, active = name };
    }

    [McpServerTool(Name = "live_agent_close")]
    [Description("[LIVE] Close a session: disconnects TCP and unregisters the slot. Params: name (required).")]
    public static object AgentClose(AgentRegistry reg, string name)
        => new { closed = reg.Remove(name), current = reg.ActiveName };

    [McpServerTool(Name = "live_agent_list")]
    [Description("[LIVE] List every open session (name, host:port, connected?, active?).")]
    public static object AgentList(AgentRegistry reg)
    {
        var active = reg.ActiveName;
        return reg.All.Select(kv => new {
            name = kv.Key,
            host = kv.Value.Host,
            port = kv.Value.Port,
            connected = kv.Value.IsConnected,
            active = string.Equals(kv.Key, active, StringComparison.OrdinalIgnoreCase),
        }).ToArray();
    }

    [McpServerTool(Name = "live_agent_current")]
    [Description("[LIVE] Return the currently-active session name (used by other LIVE tools when 'agent' is omitted).")]
    public static object AgentCurrent(AgentRegistry reg) => new { current = reg.ActiveName };

    [McpServerTool(Name = "live_agent_switch")]
    [Description("[LIVE] Switch the active session. Subsequent LIVE tools target this one when 'agent' is omitted — no reconnect needed.")]
    public static object AgentSwitch(AgentRegistry reg, string name)
    {
        reg.Switch(name);
        return new { current = name };
    }

    [McpServerTool(Name = "live_agent_list_methods")]
    [Description("[LIVE] Ask an agent to list every registered debug method (paginated). Params: offset=0, max=200, agent (optional — uses active). Response: {total, offset, returned, truncated, items}.")]
    public static object AgentListMethods(AgentRegistry reg, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("__list__"), offset, max);

    // ---- session ---------------------------------------------------------

    [McpServerTool(Name = "live_list_dotnet_processes")]
    [Description("[LIVE] List running .NET processes on the agent's host (paginated). Params: offset=0, max=200.")]
    public static object ListProcesses(AgentRegistry reg, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("session.dotnet_processes"), offset, max);

    // session.attach / session.detach / session.load_dump are intentionally NOT
    // exposed as MCP tools. An agent process is pinned to one target for its
    // lifetime — boot it with `dnspymcpagent.exe --attach <pid>` or `--dump <path>`.
    // To debug a different target, start a second agent on a different port and
    // call live_agent_open with that host:port.

    [McpServerTool(Name = "live_session_info")]
    [Description("[LIVE] Describe the agent's current debug session (pid / dump / state).")]
    public static object SessionInfo(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("session.info")!;

    [McpServerTool(Name = "live_session_go")]
    [Description("[LIVE] Continue the target (like WinDbg `g`).")]
    public static object Go(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("session.go")!;

    [McpServerTool(Name = "live_session_pause")]
    [Description("[LIVE] Break (pause) the target.")]
    public static object Pause(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("session.pause")!;

    [McpServerTool(Name = "live_session_terminate")]
    [Description("[LIVE] Terminate the target process (destructive).")]
    public static object Terminate(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("session.terminate")!;

    [McpServerTool(Name = "live_wait_paused")]
    [Description("[LIVE] Wait until the target enters Paused (breakpoint / step). Params: timeoutMs=5000.")]
    public static object WaitPaused(AgentRegistry reg, int timeoutMs = 5000, string? agent = null)
        => reg.Get(agent).Result("debug.wait_paused", new { timeoutMs })!;

    // ---- threads / stack ------------------------------------------------

    [McpServerTool(Name = "live_thread_list")]
    [Description("[LIVE] List managed threads in the target process (paginated). Params: offset=0, max=200.")]
    public static object ThreadList(AgentRegistry reg, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("thread.list"), offset, max);

    [McpServerTool(Name = "live_thread_stack")]
    [Description("[LIVE] Walk a thread's managed call stack (paginated). Params: threadId:int, offset=0, max=200. Agent walks up to (offset+max); MCP slices to envelope.")]
    public static object ThreadStack(AgentRegistry reg, int threadId, int offset = 0, int max = 200, string? agent = null)
    {
        var fetch = System.Math.Max(1, offset + System.Math.Min(max, Paging.HardMaxRows));
        return Paging.PageJsonArray(reg.Get(agent).Result("thread.stack", new { threadId, max = fetch }), offset, max);
    }

    [McpServerTool(Name = "live_thread_current")]
    [Description("[LIVE] Return which thread triggered the last pause.")]
    public static object CurrentThread(AgentRegistry reg, string? agent = null)
        => reg.Get(agent).Result("thread.current")!;

    // ---- modules --------------------------------------------------------

    [McpServerTool(Name = "live_list_modules")]
    [Description("[LIVE] List managed modules currently loaded in the attached process (paginated). Params: offset=0, max=200.")]
    public static object ListModules(AgentRegistry reg, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("module.list_live"), offset, max);

    [McpServerTool(Name = "live_find_type")]
    [Description("[LIVE] Find a type by full name across all loaded modules (paginated). Returns module path + typeDef token. Params: typeFullName, offset=0, max=200.")]
    public static object FindType(AgentRegistry reg, string typeFullName, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("module.find_type_live", new { typeFullName }), offset, max);

    [McpServerTool(Name = "live_list_type_methods")]
    [Description("[LIVE] Enumerate methods of a type inside a loaded module (paginated). Params: modulePath (path suffix ok), typeFullName, offset=0, max=200.")]
    public static object ListTypeMethods(AgentRegistry reg, string modulePath, string typeFullName, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("module.list_type_methods", new { modulePath, typeFullName }), offset, max);

    // ---- breakpoints ----------------------------------------------------

    [McpServerTool(Name = "live_bp_set_il")]
    [Description("[LIVE] Set an IL-offset breakpoint. Params: modulePath (suffix ok), token:uint, offset:uint=0.")]
    public static object BpSetIl(AgentRegistry reg, string modulePath, uint token, uint offset = 0, string? agent = null)
        => reg.Get(agent).Result("bp.set_il", new { modulePath, token, offset })!;

    [McpServerTool(Name = "live_bp_set_by_name")]
    [Description("[LIVE] Set a breakpoint at IL=0 of a named method. Params: modulePath, typeFullName, methodName, overloadIndex=0.")]
    public static object BpSetByName(AgentRegistry reg, string modulePath, string typeFullName, string methodName, int overloadIndex = 0, string? agent = null)
        => reg.Get(agent).Result("bp.set_by_name", new { modulePath, typeFullName, methodName, overloadIndex })!;

    [McpServerTool(Name = "live_bp_list")]
    [Description("[LIVE] List all breakpoints currently registered on the agent (paginated). Params: offset=0, max=200.")]
    public static object BpList(AgentRegistry reg, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("bp.list"), offset, max);

    [McpServerTool(Name = "live_bp_delete")]
    [Description("[LIVE] Delete a breakpoint by id.")]
    public static object BpDelete(AgentRegistry reg, int id, string? agent = null)
        => reg.Get(agent).Result("bp.delete", new { id })!;

    [McpServerTool(Name = "live_bp_enable")]
    [Description("[LIVE] Enable a breakpoint by id.")]
    public static object BpEnable(AgentRegistry reg, int id, string? agent = null)
        => reg.Get(agent).Result("bp.enable", new { id })!;

    [McpServerTool(Name = "live_bp_disable")]
    [Description("[LIVE] Disable a breakpoint by id (kept registered, just not active).")]
    public static object BpDisable(AgentRegistry reg, int id, string? agent = null)
        => reg.Get(agent).Result("bp.disable", new { id })!;

    // ---- stepping -------------------------------------------------------

    [McpServerTool(Name = "live_step_in")]
    [Description("[LIVE] Step into the next IL instruction on the current thread. Blocks until step completes or timeoutMs (default 5000).")]
    public static object StepIn(AgentRegistry reg, int timeoutMs = 5000, string? agent = null)
        => reg.Get(agent).Result("step.in", new { timeoutMs })!;

    [McpServerTool(Name = "live_step_over")]
    [Description("[LIVE] Step over the next IL instruction on the current thread.")]
    public static object StepOver(AgentRegistry reg, int timeoutMs = 5000, string? agent = null)
        => reg.Get(agent).Result("step.over", new { timeoutMs })!;

    [McpServerTool(Name = "live_step_out")]
    [Description("[LIVE] Step out of the current function.")]
    public static object StepOut(AgentRegistry reg, int timeoutMs = 5000, string? agent = null)
        => reg.Get(agent).Result("step.out", new { timeoutMs })!;

    // ---- heap / memory --------------------------------------------------

    [McpServerTool(Name = "live_heap_find_instances")]
    [Description("[LIVE/DUMP] Find managed object addresses by type name (substring ok, paginated). Params: typeName, offset=0, max=200. Agent walks heap up to (offset+max); MCP slices to envelope. Bump offset to continue past truncation.")]
    public static object HeapFind(AgentRegistry reg, string typeName, int offset = 0, int max = 200, string? agent = null)
    {
        var fetch = System.Math.Max(1, offset + System.Math.Min(max, Paging.HardMaxRows));
        return Paging.PageJsonArray(reg.Get(agent).Result("heap.find_instances", new { typeName, max = fetch }), offset, max);
    }

    [McpServerTool(Name = "live_heap_read_object")]
    [Description("[LIVE/DUMP] Dump fields of a managed object. Params: address:ulong, maxFields=64.")]
    public static object HeapReadObject(AgentRegistry reg, ulong address, int maxFields = 64, string? agent = null)
        => reg.Get(agent).Result("heap.read_object", new { address, maxFields })!;

    [McpServerTool(Name = "live_heap_read_string")]
    [Description("[LIVE/DUMP] Read a System.String at a managed address.")]
    public static object HeapReadString(AgentRegistry reg, ulong address, string? agent = null)
        => reg.Get(agent).Result("heap.read_string", new { address })!;

    [McpServerTool(Name = "live_heap_stats")]
    [Description("[LIVE/DUMP] Top-N types on the managed heap by total size (paginated). Params: top=25 (agent-side), offset=0, max=200 (MCP envelope cap).")]
    public static object HeapStats(AgentRegistry reg, int top = 25, int offset = 0, int max = 200, string? agent = null)
        => Paging.PageJsonArray(reg.Get(agent).Result("heap.stats", new { top }), offset, max);

    [McpServerTool(Name = "live_memory_read")]
    [Description("[LIVE/DUMP] Read raw bytes (returned as hex) at a virtual address. Params: address:ulong, size:int (1..1MB).")]
    public static object MemoryRead(AgentRegistry reg, ulong address, int size, string? agent = null)
        => reg.Get(agent).Result("memory.read", new { address, size })!;

    [McpServerTool(Name = "live_memory_write")]
    [Description("[LIVE] Write raw bytes (hex string) at a virtual address — live edit against the running process, not a file patch. Use file_patch_bytes for on-disk edits.")]
    public static object MemoryWrite(AgentRegistry reg, ulong address, string hex, string? agent = null)
        => reg.Get(agent).Result("memory.write", new { address, hex })!;

    [McpServerTool(Name = "live_memory_read_int")]
    [Description("[LIVE/DUMP] Read a typed integer from memory. kind in {i8,u8,i16,u16,i32,u32,i64,u64}, default 'i32'.")]
    public static object MemoryReadInt(AgentRegistry reg, ulong address, string kind = "i32", string? agent = null)
        => reg.Get(agent).Result("memory.read_int", new { address, kind })!;

    [McpServerTool(Name = "live_disasm")]
    [Description("[LIVE/DUMP] Disassemble x64 bytes at a virtual address via Iced. Params: address:ulong, size=128.")]
    public static object Disasm(AgentRegistry reg, ulong address, int size = 128, string? agent = null)
        => reg.Get(agent).Result("memory.disasm", new { address, size })!;
}
