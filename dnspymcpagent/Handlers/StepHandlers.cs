using System;
using System.Threading;
using dndbg.Engine;
using DnSpyMcp.Agent.Services;

namespace DnSpyMcp.Agent.Handlers;

public static class StepHandlers
{
    public static void Register(Dispatcher d)
    {
        d.Register("step.in",
            "[LIVE] Step into the next IL instruction on the current thread. Waits until stepping completes or timeoutMs (default 5000).",
            p => DoStep(p, "in"));

        d.Register("step.over",
            "[LIVE] Step over the next IL instruction on the current thread.",
            p => DoStep(p, "over"));

        d.Register("step.out",
            "[LIVE] Step out of the current function.",
            p => DoStep(p, "out"));

        d.Register("debug.wait_paused",
            "[LIVE] Block until the target is Paused (breakpoint, step complete, or pause). Params: {timeoutMs?:int=5000}.",
            p =>
            {
                var timeout = Dispatcher.Opt<int>(p, "timeoutMs", 5000);
                var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
                while (DateTime.UtcNow < deadline)
                {
                    var state = Program.Session.OnDbg(() => Program.Session.DnDebugger.ProcessState);
                    if (state == DebuggerProcessState.Paused || state == DebuggerProcessState.Terminated)
                        return new { state = state.ToString() };
                    Thread.Sleep(50);
                }
                return new { state = Program.Session.OnDbg(() => Program.Session.DnDebugger.ProcessState.ToString()), timedOut = true };
            });
    }

    private static object DoStep(Newtonsoft.Json.Linq.JObject? p, string kind)
    {
        var timeout = Dispatcher.Opt<int>(p, "timeoutMs", 5000);
        var done = new ManualResetEventSlim(false);

        Program.Session.OnDbg(() =>
        {
            var dbg = Program.Session.DnDebugger;
            if (dbg.ProcessState != DebuggerProcessState.Paused)
                throw new InvalidOperationException($"cannot step: state={dbg.ProcessState}");

            var frame = dbg.Current.ILFrame;
            Action<DnDebugger, StepCompleteDebugCallbackEventArgs?, bool> onDone =
                (_, _, _) => done.Set();

            CorStepper? stepper = kind switch
            {
                "in"   => dbg.StepInto(frame, onDone),
                "over" => dbg.StepOver(frame, onDone),
                "out"  => dbg.StepOut(frame, onDone),
                _      => throw new InvalidOperationException("bad step kind"),
            };
            if (stepper == null) throw new InvalidOperationException("failed to create stepper");

            if (dbg.ProcessState == DebuggerProcessState.Paused)
                dbg.Continue();
        });

        bool ok = done.Wait(timeout);
        var state = Program.Session.OnDbg(() => Program.Session.DnDebugger.ProcessState.ToString());
        return new { ok, timedOut = !ok, state };
    }
}
