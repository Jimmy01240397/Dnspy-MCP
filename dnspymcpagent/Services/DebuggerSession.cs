using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

            // OnAttachComplete fires on the STA after the CLR's attach-time callback burst
            // (CreateProcess / CreateAppDomain / LoadAssembly / LoadModule / CreateThread) has
            // finished draining. Subscribe inside the same STA Invoke that calls Attach, before
            // yielding back to the dispatcher — otherwise the burst could race ahead of us.
            var attachComplete = new ManualResetEventSlim(false);
            Exception? attachError = null;

            _dnDebugger = _dbgThread.Invoke(() =>
            {
                var attachInfo = new DesktopCLRTypeAttachInfo(string.Empty);
                // DebugOptions.DebugOptionsProvider is non-nullable — if left null, the
                // CreateProcess callback handler inside dndbg dereferences it and throws
                // NRE. The exception is swallowed by OnManagedCallbackFromAnyThread2 and
                // Continue() is never called, which stalls the entire callback burst.
                var options = new AttachProcessOptions(attachInfo)
                {
                    ProcessId = pid,
                    DebugMessageDispatcher = _dbgThread.GetDebugMessageDispatcher(),
                    DebugOptions = new DebugOptions { DebugOptionsProvider = new DefaultDebugOptionsProvider() },
                };
                var dbg = DnDebugger.Attach(options);

                // If DebugActiveProcess HRESULT'd we'd have no processes — fail loud, don't
                // return a half-dead DnDebugger to the caller.
                if (dbg.Processes.Length == 0)
                {
                    attachError = new InvalidOperationException(
                        $"ICorDebug attach returned no processes for pid={pid} (DebugActiveProcess failed — process gone, wrong CLR version, already being debugged, or access denied)");
                    return dbg;
                }

                // Krafs.Publicizer republishes the internal backing field alongside the public
                // event, so direct `dbg.OnAttachComplete += ...` fails to compile with CS0229.
                // Subscribe via reflection — reflection sees the event, not the field.
                var evt = typeof(DnDebugger).GetEvent("OnAttachComplete")
                    ?? throw new InvalidOperationException("DnDebugger.OnAttachComplete event missing");
                EventHandler handler = (_, _) => attachComplete.Set();
                evt.AddEventHandler(dbg, handler);
                return dbg;
            });

            if (attachError != null)
                throw attachError;

            Pid = pid;
            DumpPath = null;

            // Pump the attach-time callback burst and block until the CLR signals it's done.
            // The burst runs on the STA dispatcher, so we must NOT hold an Invoke here —
            // wait on the MRE instead. 15s is generous; real-world attach completes in <500ms.
            if (!attachComplete.Wait(TimeSpan.FromSeconds(15)))
            {
                var diag = DescribeBootstrapState();
                Detach();
                throw new TimeoutException(
                    $"ICorDebug attach-bootstrap timed out after 15s. Diagnostic: {diag}");
            }

            // Sanity check — Processes was non-empty pre-wait, but modules might still be
            // empty if the process has no managed code loaded (pathological, but explicit
            // error beats a silent half-state).
            var (procCount, adCount, modCount, thrCount) = ReadBootstrapState();
            if (procCount == 0 || adCount == 0 || modCount == 0)
            {
                Detach();
                throw new InvalidOperationException(
                    $"attach completed but state is empty (procs={procCount}, appdomains={adCount}, modules={modCount}, threads={thrCount})");
            }

            // Also attach ClrMD for passive read (heap walk etc.). Passive = non-invasive.
            // Done AFTER ICorDebug bootstrap so the process is in a stable state.
            _clrMdTarget = DataTarget.AttachToProcess(pid, false);
            _clrRuntime = _clrMdTarget.ClrVersions.FirstOrDefault()?.CreateRuntime();
        }
    }

    private (int processes, int appDomains, int modules, int threads) ReadBootstrapState()
    {
        int p = 0, a = 0, m = 0, t = 0;
        try
        {
            _dbgThread!.Invoke(() =>
            {
                foreach (var proc in _dnDebugger!.Processes)
                {
                    p++;
                    foreach (var th in proc.Threads) t++;
                    foreach (var ad in proc.AppDomains)
                    {
                        a++;
                        foreach (var asm in ad.Assemblies)
                            foreach (var _ in asm.Modules) m++;
                    }
                }
            });
        }
        catch { /* STA gone */ }
        return (p, a, m, t);
    }

    private string DescribeBootstrapState()
    {
        var (p, a, m, t) = ReadBootstrapState();
        return $"processes={p}, appDomains={a}, modules={m}, threads={t}";
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

    /// <summary>
    /// Run a callback on the debugger STA thread (required for all ICorDebug calls).
    /// The wrapper catches any exception thrown by <paramref name="fn"/> so it never
    /// escapes to dnSpy's dispatcher — an unhandled exception there kills the STA
    /// thread and ultimately the whole process.
    /// </summary>
    public T OnDbg<T>(Func<T> fn)
    {
        if (_dbgThread == null) throw new InvalidOperationException("no debugger thread (not attached)");
        T value = default!;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? caught = null;
        _dbgThread.Invoke(() =>
        {
            try { value = fn(); }
            catch (Exception ex) { caught = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
        });
        caught?.Throw();
        return value;
    }

    public void OnDbg(Action fn) => OnDbg<object?>(() => { fn(); return null; });

    public void Dispose() => Detach();
}
