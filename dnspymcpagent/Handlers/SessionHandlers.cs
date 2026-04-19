using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DnSpyMcp.Agent.Services;
using Newtonsoft.Json.Linq;

namespace DnSpyMcp.Agent.Handlers;

public static class SessionHandlers
{
    public static void Register(Dispatcher d)
    {
        // Attach / detach / load-dump are NOT exposed as runtime methods on purpose.
        // The design is "one agent process == one target": boot the agent with
        // `--attach <pid>` or `--dump <path>` and it stays pinned to that target
        // until it exits. To talk to a different target, start another agent on a
        // different port and let the MCP server connect to it via live_agent_connect.

        d.Register("session.info",
            "[LIVE] Describe the current debug session (attached pid / loaded dump).",
            _ => new
            {
                isAttached = Program.Session.IsAttached,
                isDump = Program.Session.IsDump,
                pid = Program.Session.Pid,
                dumpPath = Program.Session.DumpPath,
                description = Program.Session.Describe(),
            });

        d.Register("session.dotnet_processes",
            "List .NET processes on this machine (has CLR loaded). Use to pick a PID for `dnspymcpagent --attach`.",
            _ =>
            {
                var rows = new List<object>();
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        bool hasClr = proc.Modules.Cast<ProcessModule>().Any(m =>
                            (m.ModuleName?.StartsWith("coreclr", StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (m.ModuleName?.StartsWith("clr.dll", StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (m.ModuleName?.Equals("mscorwks.dll", StringComparison.OrdinalIgnoreCase) ?? false));
                        if (hasClr) rows.Add(new { pid = proc.Id, name = proc.ProcessName });
                    }
                    catch { /* access denied: skip */ }
                }
                return rows;
            });

        d.Register("session.go",
            "[LIVE] Continue a paused target (like WinDbg `g`).",
            _ =>
            {
                Program.Session.OnDbg(() =>
                {
                    if (Program.Session.DnDebugger.ProcessState == dndbg.Engine.DebuggerProcessState.Paused)
                        Program.Session.DnDebugger.Continue();
                });
                return new { state = Program.Session.OnDbg(() => Program.Session.DnDebugger.ProcessState.ToString()) };
            });

        d.Register("session.pause",
            "[LIVE] Break (pause) the target.",
            _ =>
            {
                Program.Session.OnDbg(() => Program.Session.DnDebugger.TryBreakProcesses());
                return new { state = Program.Session.OnDbg(() => Program.Session.DnDebugger.ProcessState.ToString()) };
            });

        d.Register("session.terminate",
            "[LIVE] Terminate the target process (destructive).",
            _ =>
            {
                Program.Session.OnDbg(() => Program.Session.DnDebugger.TerminateProcesses());
                return new { terminated = true };
            });
    }
}
