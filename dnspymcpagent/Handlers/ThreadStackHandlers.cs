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
            "[DEBUG] List all managed threads in the attached process. Includes uniqueId, osThreadId, volatileThreadId, frameCount.",
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
            "[DEBUG] Return the managed call stack for a thread. Identify the thread with EITHER uniqueId (debugger-assigned) OR osThreadId. Legacy alias: threadId == uniqueId. Params: {uniqueId?:int, osThreadId?:int, threadId?:int, max?:int=32}.",
            p => Program.Session.OnDbg(() =>
            {
                int? uniqueId = p != null && p.TryGetValue("uniqueId", System.StringComparison.OrdinalIgnoreCase, out var uTok) && uTok.Type != JTokenType.Null
                    ? uTok.ToObject<int>()
                    : (int?)null;
                int? osThreadId = p != null && p.TryGetValue("osThreadId", System.StringComparison.OrdinalIgnoreCase, out var oTok) && oTok.Type != JTokenType.Null
                    ? oTok.ToObject<int>()
                    : (int?)null;
                if (!uniqueId.HasValue && p != null && p.TryGetValue("threadId", System.StringComparison.OrdinalIgnoreCase, out var lTok) && lTok.Type != JTokenType.Null)
                    uniqueId = lTok.ToObject<int>();

                int provided = (uniqueId.HasValue ? 1 : 0) + (osThreadId.HasValue ? 1 : 0);
                if (provided == 0) throw new System.ArgumentException("supply uniqueId or osThreadId");
                if (provided == 2) throw new System.ArgumentException("pass exactly one of uniqueId / osThreadId");

                var max = Dispatcher.Opt<int>(p, "max", 32);
                var dbg = Program.Session.DnDebugger;

                DnThread? target = null;
                foreach (var proc in dbg.Processes)
                    foreach (var t in proc.Threads)
                    {
                        bool match = uniqueId.HasValue ? t.UniqueId == uniqueId.Value : t.ThreadId == osThreadId!.Value;
                        if (match) { target = t; break; }
                    }
                if (target == null)
                {
                    var label = uniqueId.HasValue ? $"uniqueId={uniqueId.Value}" : $"osThreadId={osThreadId!.Value}";
                    throw new System.ArgumentException($"thread {label} not found");
                }

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
            "[DEBUG] Return the thread that triggered the last pause (breakpoint / exception / step).",
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
