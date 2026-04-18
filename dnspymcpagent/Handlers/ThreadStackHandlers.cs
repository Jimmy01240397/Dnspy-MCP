using System.Collections.Generic;
using System.Linq;
using dndbg.Engine;
using DnSpyMcp.Agent.Services;
using Newtonsoft.Json.Linq;

namespace DnSpyMcp.Agent.Handlers;

public static class ThreadStackHandlers
{
    public static void Register(Dispatcher d)
    {
        d.Register("thread.list",
            "[LIVE] List all managed threads in the attached process. Includes uniqueId, osThreadId, volatileThreadId, frameCount.",
            _ => Program.Session.OnDbg(() =>
            {
                var dbg = Program.Session.DnDebugger;
                var result = new List<object>();
                foreach (var proc in dbg.Processes)
                {
                    foreach (var t in proc.Threads)
                    {
                        int frameCount = 0;
                        try { foreach (var c in t.CorThread.Chains) frameCount += c.Frames.Count(); } catch { }
                        result.Add(new
                        {
                            uniqueId = t.UniqueId,
                            osThreadId = t.ThreadId,
                            volatileThreadId = t.VolatileThreadId,
                            frameCount,
                        });
                    }
                }
                return result;
            }));

        d.Register("thread.stack",
            "[LIVE] Return the managed call stack for a given thread uniqueId. Params: {threadId:int, max?:int=32}.",
            p => Program.Session.OnDbg(() =>
            {
                var threadId = Dispatcher.Req<int>(p, "threadId");
                var max = Dispatcher.Opt<int>(p, "max", 32);
                var dbg = Program.Session.DnDebugger;

                DnThread? target = null;
                foreach (var proc in dbg.Processes)
                    foreach (var t in proc.Threads)
                        if (t.UniqueId == threadId) { target = t; break; }
                if (target == null) throw new System.ArgumentException($"thread {threadId} not found");

                var frames = new List<object>();
                int idx = 0;
                foreach (var chain in target.CorThread.Chains)
                {
                    foreach (var f in chain.Frames)
                    {
                        if (idx >= max) break;
                        string? methodName = null, moduleName = null;
                        uint token = f.Token;
                        try
                        {
                            var fn = f.Function;
                            if (fn != null)
                            {
                                var mod = fn.Module;
                                moduleName = mod?.Name;
                                var mdi = mod?.GetMetaDataInterface<dndbg.COM.MetaData.IMetaDataImport>();
                                if (mdi != null) methodName = MetaDataUtils.FullMethodName(mdi, token);
                            }
                        }
                        catch { }
                        frames.Add(new
                        {
                            index = idx,
                            token,
                            ilOffset = (int)f.ILFrameIP.Offset,
                            mapping = f.ILFrameIP.Mapping.ToString(),
                            nativeOffset = (long)f.NativeFrameIP,
                            stackStart = (long)f.StackStart,
                            stackEnd = (long)f.StackEnd,
                            method = methodName,
                            module = moduleName,
                            isNative = f.IsNativeFrame,
                            isIL = f.IsILFrame,
                            isInternal = f.IsInternalFrame,
                        });
                        idx++;
                    }
                    if (idx >= max) break;
                }
                return frames;
            }));

        d.Register("thread.current",
            "[LIVE] Return the thread that triggered the last pause (breakpoint / exception / step).",
            _ => Program.Session.OnDbg<object>(() =>
            {
                var cur = Program.Session.DnDebugger.Current.Thread;
                if (cur == null) return new { present = false, uniqueId = (int?)null, osThreadId = (int?)null, volatileThreadId = (int?)null };
                return new
                {
                    present = true,
                    uniqueId = (int?)cur.UniqueId,
                    osThreadId = (int?)cur.ThreadId,
                    volatileThreadId = (int?)cur.VolatileThreadId,
                };
            }));
    }
}
