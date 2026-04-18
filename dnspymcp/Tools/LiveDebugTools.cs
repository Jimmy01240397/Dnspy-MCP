using System.ComponentModel;
using DnSpyMcp.Services;
using ModelContextProtocol.Server;

namespace DnSpyMcp.Tools;

/// <summary>
/// Thin proxies over the dnspymcpagent TCP+NDJSON backend. Every tool here
/// is [LIVE] — it talks to a running / dumped .NET process and only works
/// after `live_agent_connect` succeeds.
/// </summary>
[McpServerToolType]
public static class LiveDebugTools
{
    [McpServerTool(Name = "live_agent_connect")]
    [Description("[LIVE] Open the persistent TCP connection to a dnspymcpagent. Required before any live_* tool. Params: host='127.0.0.1', port=5555, token=null.")]
    public static object AgentConnect(AgentClient agent, string host = "127.0.0.1", int port = 5555, string? token = null)
    {
        agent.Configure(host, port, token);
        agent.Connect();
        return new { connected = true, host, port };
    }

    [McpServerTool(Name = "live_agent_disconnect")]
    [Description("[LIVE] Close the TCP connection to the agent.")]
    public static object AgentDisconnect(AgentClient agent)
    {
        agent.Close();
        return new { disconnected = true };
    }

    [McpServerTool(Name = "live_agent_list_methods")]
    [Description("[LIVE] Ask the agent to list every registered debug method (name + description). Good for tool discovery.")]
    public static object AgentList(AgentClient agent) => agent.Result("__list__");

    // ---- session ---------------------------------------------------------

    [McpServerTool(Name = "live_list_dotnet_processes")]
    [Description("[LIVE] List running .NET processes on the agent's host so you can pick a PID.")]
    public static object ListProcesses(AgentClient agent) => agent.Result("session.dotnet_processes");

    [McpServerTool(Name = "live_session_attach")]
    [Description("[LIVE] Attach the agent to a .NET PID.")]
    public static object Attach(AgentClient agent, int pid) => agent.Result("session.attach", new { pid });

    [McpServerTool(Name = "live_session_load_dump")]
    [Description("[LIVE] Have the agent open a crash dump (.dmp) via ClrMD — heap/memory tools work, live debug tools do not.")]
    public static object LoadDump(AgentClient agent, string path) => agent.Result("session.load_dump", new { path });

    [McpServerTool(Name = "live_session_detach")]
    [Description("[LIVE] Detach / unload the current debug target.")]
    public static object Detach(AgentClient agent) => agent.Result("session.detach");

    [McpServerTool(Name = "live_session_info")]
    [Description("[LIVE] Describe the agent's current debug session (pid / dump / state).")]
    public static object SessionInfo(AgentClient agent) => agent.Result("session.info");

    [McpServerTool(Name = "live_session_go")]
    [Description("[LIVE] Continue the target (like WinDbg `g`).")]
    public static object Go(AgentClient agent) => agent.Result("session.go");

    [McpServerTool(Name = "live_session_pause")]
    [Description("[LIVE] Break (pause) the target.")]
    public static object Pause(AgentClient agent) => agent.Result("session.pause");

    [McpServerTool(Name = "live_session_terminate")]
    [Description("[LIVE] Terminate the target process (destructive).")]
    public static object Terminate(AgentClient agent) => agent.Result("session.terminate");

    [McpServerTool(Name = "live_wait_paused")]
    [Description("[LIVE] Wait until the target enters Paused (breakpoint / step). Params: timeoutMs=5000.")]
    public static object WaitPaused(AgentClient agent, int timeoutMs = 5000)
        => agent.Result("debug.wait_paused", new { timeoutMs });

    // ---- threads / stack ------------------------------------------------

    [McpServerTool(Name = "live_thread_list")]
    [Description("[LIVE] List managed threads in the target process.")]
    public static object ThreadList(AgentClient agent) => agent.Result("thread.list");

    [McpServerTool(Name = "live_thread_stack")]
    [Description("[LIVE] Walk a thread's managed call stack. Params: threadId:int, max=32.")]
    public static object ThreadStack(AgentClient agent, int threadId, int max = 32)
        => agent.Result("thread.stack", new { threadId, max });

    [McpServerTool(Name = "live_thread_current")]
    [Description("[LIVE] Return which thread triggered the last pause.")]
    public static object CurrentThread(AgentClient agent) => agent.Result("thread.current");

    // ---- modules --------------------------------------------------------

    [McpServerTool(Name = "live_list_modules")]
    [Description("[LIVE] List managed modules currently loaded in the attached process.")]
    public static object ListModules(AgentClient agent) => agent.Result("module.list_live");

    [McpServerTool(Name = "live_find_type")]
    [Description("[LIVE] Find a type by full name across all loaded modules (returns module path + typeDef token).")]
    public static object FindType(AgentClient agent, string typeFullName)
        => agent.Result("module.find_type_live", new { typeFullName });

    [McpServerTool(Name = "live_list_type_methods")]
    [Description("[LIVE] Enumerate methods of a type inside a loaded module. Params: modulePath (path suffix ok), typeFullName.")]
    public static object ListTypeMethods(AgentClient agent, string modulePath, string typeFullName)
        => agent.Result("module.list_type_methods", new { modulePath, typeFullName });

    // ---- breakpoints ----------------------------------------------------

    [McpServerTool(Name = "live_bp_set_il")]
    [Description("[LIVE] Set an IL-offset breakpoint. Params: modulePath (suffix ok), token:uint, offset:uint=0.")]
    public static object BpSetIl(AgentClient agent, string modulePath, uint token, uint offset = 0)
        => agent.Result("bp.set_il", new { modulePath, token, offset });

    [McpServerTool(Name = "live_bp_set_by_name")]
    [Description("[LIVE] Set a breakpoint at IL=0 of a named method. Params: modulePath, typeFullName, methodName, overloadIndex=0.")]
    public static object BpSetByName(AgentClient agent, string modulePath, string typeFullName, string methodName, int overloadIndex = 0)
        => agent.Result("bp.set_by_name", new { modulePath, typeFullName, methodName, overloadIndex });

    [McpServerTool(Name = "live_bp_list")]
    [Description("[LIVE] List all breakpoints currently registered on the agent.")]
    public static object BpList(AgentClient agent) => agent.Result("bp.list");

    [McpServerTool(Name = "live_bp_delete")]
    [Description("[LIVE] Delete a breakpoint by id.")]
    public static object BpDelete(AgentClient agent, int id) => agent.Result("bp.delete", new { id });

    [McpServerTool(Name = "live_bp_enable")]
    [Description("[LIVE] Enable a breakpoint by id.")]
    public static object BpEnable(AgentClient agent, int id) => agent.Result("bp.enable", new { id });

    [McpServerTool(Name = "live_bp_disable")]
    [Description("[LIVE] Disable a breakpoint by id (kept registered, just not active).")]
    public static object BpDisable(AgentClient agent, int id) => agent.Result("bp.disable", new { id });

    // ---- stepping -------------------------------------------------------

    [McpServerTool(Name = "live_step_in")]
    [Description("[LIVE] Step into the next IL instruction on the current thread. Blocks until step completes or timeoutMs (default 5000).")]
    public static object StepIn(AgentClient agent, int timeoutMs = 5000)
        => agent.Result("step.in", new { timeoutMs });

    [McpServerTool(Name = "live_step_over")]
    [Description("[LIVE] Step over the next IL instruction on the current thread.")]
    public static object StepOver(AgentClient agent, int timeoutMs = 5000)
        => agent.Result("step.over", new { timeoutMs });

    [McpServerTool(Name = "live_step_out")]
    [Description("[LIVE] Step out of the current function.")]
    public static object StepOut(AgentClient agent, int timeoutMs = 5000)
        => agent.Result("step.out", new { timeoutMs });

    // ---- heap / memory --------------------------------------------------

    [McpServerTool(Name = "live_heap_find_instances")]
    [Description("[LIVE/DUMP] Find managed object addresses by type name (substring ok). Params: typeName, max=256.")]
    public static object HeapFind(AgentClient agent, string typeName, int max = 256)
        => agent.Result("heap.find_instances", new { typeName, max });

    [McpServerTool(Name = "live_heap_read_object")]
    [Description("[LIVE/DUMP] Dump fields of a managed object. Params: address:ulong, maxFields=64.")]
    public static object HeapReadObject(AgentClient agent, ulong address, int maxFields = 64)
        => agent.Result("heap.read_object", new { address, maxFields });

    [McpServerTool(Name = "live_heap_read_string")]
    [Description("[LIVE/DUMP] Read a System.String at a managed address.")]
    public static object HeapReadString(AgentClient agent, ulong address)
        => agent.Result("heap.read_string", new { address });

    [McpServerTool(Name = "live_heap_stats")]
    [Description("[LIVE/DUMP] Top-N types on the managed heap by total size.")]
    public static object HeapStats(AgentClient agent, int top = 25)
        => agent.Result("heap.stats", new { top });

    [McpServerTool(Name = "live_memory_read")]
    [Description("[LIVE/DUMP] Read raw bytes (returned as hex) at a virtual address. Params: address:ulong, size:int (1..1MB).")]
    public static object MemoryRead(AgentClient agent, ulong address, int size)
        => agent.Result("memory.read", new { address, size });

    [McpServerTool(Name = "live_memory_write")]
    [Description("[LIVE] Write raw bytes (hex string) at a virtual address — this is a *live* edit against the running process, not a file patch. Use file_patch_bytes for on-disk edits.")]
    public static object MemoryWrite(AgentClient agent, ulong address, string hex)
        => agent.Result("memory.write", new { address, hex });

    [McpServerTool(Name = "live_memory_read_int")]
    [Description("[LIVE/DUMP] Read a typed integer from memory. kind in {i8,u8,i16,u16,i32,u32,i64,u64}, default 'i32'.")]
    public static object MemoryReadInt(AgentClient agent, ulong address, string kind = "i32")
        => agent.Result("memory.read_int", new { address, kind });

    [McpServerTool(Name = "live_disasm")]
    [Description("[LIVE/DUMP] Disassemble x64 bytes at a virtual address via Iced. Params: address:ulong, size=128.")]
    public static object Disasm(AgentClient agent, ulong address, int size = 128)
        => agent.Result("memory.disasm", new { address, size });
}
