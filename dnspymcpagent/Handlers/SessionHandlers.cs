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
        // Attach / detach are runtime-controllable — one agent process can be
        // repointed at any local PID across its lifetime, no restart required.
        // Target process death auto-detaches; the agent itself keeps listening.
        // Load-dump stays startup-only (dumps are immutable by nature).

        d.Register("session.attach",
            "[LIVE] Attach the debugger to a local .NET process by PID. If already attached elsewhere, detaches first. Params: pid:int.",
            args =>
            {
                if (args is not JObject obj || obj["pid"] == null)
                    throw new ArgumentException("pid (int) is required");
                int pid = obj["pid"]!.Value<int>();
                Program.Session.Attach(pid);
                return new
                {
                    attached = Program.Session.IsAttached,
                    pid = Program.Session.Pid,
                    description = Program.Session.Describe(),
                };
            });

        d.Register("session.detach",
            "[LIVE] Detach from the current target. Agent keeps listening. Idempotent — detach without attach is a no-op.",
            _ =>
            {
                bool wasAttached = Program.Session.IsAttached || Program.Session.IsDump;
                Program.Session.Detach();
                return new
                {
                    detached = wasAttached,
                    lastExitedPid = Program.Session.LastExitedPid,
                    lastExitReason = Program.Session.LastExitReason,
                    lastExitUtc = Program.Session.LastExitUtc?.ToString("o"),
                };
            });

        d.Register("session.info",
            "[LIVE] Describe the current debug session (attached pid / loaded dump / last exit info if any).",
            _ => new
            {
                isAttached = Program.Session.IsAttached,
                isDump = Program.Session.IsDump,
                pid = Program.Session.Pid,
                dumpPath = Program.Session.DumpPath,
                description = Program.Session.Describe(),
                lastExitedPid = Program.Session.LastExitedPid,
                lastExitReason = Program.Session.LastExitReason,
                lastExitUtc = Program.Session.LastExitUtc?.ToString("o"),
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
