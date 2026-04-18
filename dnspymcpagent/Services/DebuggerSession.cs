using System;
using System.Collections.Generic;
using System.Linq;
using dndbg.Engine;
using dnSpy.Debugger.DotNet.CorDebug.Impl;
using Microsoft.Diagnostics.Runtime;

namespace DnSpyMcp.Agent.Services;

/// <summary>
/// Holds the single active debugger target.
/// Uses dnSpy's <see cref="DebuggerThread"/> to serialize ICorDebug calls onto an STA thread.
/// ClrMD is used in parallel for passive heap inspection (works when attached or from a dump).
/// </summary>
public sealed class DebuggerSession : IDisposable
{
    private readonly object _lock = new();
    private DebuggerThread? _dbgThread;
    private DnDebugger? _dnDebugger;
    private DataTarget? _clrMdTarget;
    private ClrRuntime? _clrRuntime;

    public int? Pid { get; private set; }
    public string? DumpPath { get; private set; }
    public bool IsAttached => _dnDebugger != null;
    public bool IsDump => _clrMdTarget != null && _dnDebugger == null;

    public readonly BreakpointRegistry Breakpoints = new();

    public DnDebugger DnDebugger =>
        _dnDebugger ?? throw new InvalidOperationException("Not attached. Call /tool/session/attach first.");

    public DebuggerThread DbgThread =>
        _dbgThread ?? throw new InvalidOperationException("Debugger thread not running.");

    public ClrRuntime ClrRuntime =>
        _clrRuntime ?? throw new InvalidOperationException("No ClrMD runtime (not attached / no dump).");

    public DataTarget ClrMdTarget =>
        _clrMdTarget ?? throw new InvalidOperationException("No ClrMD target.");

    public void Attach(int pid)
    {
        lock (_lock)
        {
            Detach();

            _dbgThread = new DebuggerThread("dnspymcp-dbg");
            _dbgThread.CallDispatcherRun();
            _dnDebugger = _dbgThread.Invoke(() =>
            {
                var attachInfo = new DesktopCLRTypeAttachInfo(string.Empty);
                var options = new AttachProcessOptions(attachInfo)
                {
                    ProcessId = pid,
                    DebugMessageDispatcher = _dbgThread.GetDebugMessageDispatcher(),
                    DebugOptions = new DebugOptions(),
                };
                return DnDebugger.Attach(options);
            });

            // Also attach ClrMD for passive read (heap walk etc.). Passive = non-invasive.
            _clrMdTarget = DataTarget.AttachToProcess(pid, false);
            _clrRuntime = _clrMdTarget.ClrVersions.FirstOrDefault()?.CreateRuntime();

            Pid = pid;
            DumpPath = null;
        }
    }

    public void LoadDump(string path)
    {
        lock (_lock)
        {
            Detach();
            _clrMdTarget = DataTarget.LoadDump(path);
            _clrRuntime = _clrMdTarget.ClrVersions.FirstOrDefault()?.CreateRuntime();
            DumpPath = path;
            Pid = null;
        }
    }

    public void Detach()
    {
        lock (_lock)
        {
            if (_dnDebugger != null)
            {
                try
                {
                    _dbgThread?.Invoke(() =>
                    {
                        try { _dnDebugger.TerminateProcesses(); } catch { /* ignore */ }
                    });
                }
                catch { /* ignore */ }
                _dnDebugger = null;
            }

            if (_dbgThread != null)
            {
                try { _dbgThread.Terminate(); } catch { /* ignore */ }
                _dbgThread = null;
            }

            _clrRuntime = null;
            _clrMdTarget?.Dispose();
            _clrMdTarget = null;

            Breakpoints.Clear();
            Pid = null;
            DumpPath = null;
        }
    }

    public string Describe()
    {
        if (_dnDebugger != null)
        {
            var clr = _clrRuntime?.ClrInfo.Version.ToString() ?? "?";
            return $"attached pid={Pid} CLR={clr}";
        }
        if (_clrMdTarget != null && DumpPath != null) return $"dump {DumpPath}";
        return "no target";
    }

    /// <summary>Run a callback on the debugger STA thread (required for all ICorDebug calls).</summary>
    public T OnDbg<T>(Func<T> fn)
    {
        if (_dbgThread == null) throw new InvalidOperationException("no debugger thread (not attached)");
        return _dbgThread.Invoke(fn);
    }

    public void OnDbg(Action fn) => OnDbg<object?>(() => { fn(); return null; });

    public void Dispose() => Detach();
}
