using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dndbg.Engine;
using DnSpyMcp.Agent.Services;
using Newtonsoft.Json.Linq;

namespace DnSpyMcp.Agent.Handlers;

public static class BreakpointHandlers
{
    public static void Register(Dispatcher d)
    {
        d.Register("bp.set_il",
            "[LIVE] Set an IL-offset breakpoint. Params: {modulePath:string, token:uint, offset?:uint=0}. modulePath matches DnModule.Name suffix (case-insensitive).",
            p => Program.Session.OnDbg(() =>
            {
                var modulePath = Dispatcher.Req<string>(p, "modulePath");
                var token = Dispatcher.Req<uint>(p, "token");
                var offset = Dispatcher.Opt<uint>(p, "offset", 0);

                var mod = FindModule(modulePath)
                    ?? throw new ArgumentException($"module not found: {modulePath}");

                var bp = Program.Session.DnDebugger.CreateBreakpoint(mod.DnModuleId, token, offset, null);
                var entry = Program.Session.Breakpoints.Add(
                    "il",
                    $"IL bp {Path.GetFileName(mod.Name)}!0x{token:X8}+{offset}",
                    bp);
                return Describe(entry);
            }));

        d.Register("bp.set_by_name",
            "[LIVE] Set a breakpoint at IL=0 of a method identified by type and method name. Params: {modulePath:string, typeFullName:string, methodName:string, overloadIndex?:int=0}.",
            p => Program.Session.OnDbg(() =>
            {
                var modulePath = Dispatcher.Req<string>(p, "modulePath");
                var typeFullName = Dispatcher.Req<string>(p, "typeFullName");
                var methodName = Dispatcher.Req<string>(p, "methodName");
                var overloadIndex = Dispatcher.Opt<int>(p, "overloadIndex", 0);

                var mod = FindModule(modulePath)
                    ?? throw new ArgumentException($"module not found: {modulePath}");
                var mdi = mod.CorModule.GetMetaDataInterface<dndbg.COM.MetaData.IMetaDataImport>()
                    ?? throw new InvalidOperationException("failed to get IMetaDataImport");

                var typeToken = MetaDataUtils.FindTypeDefByName(mdi, typeFullName);
                if (typeToken == 0) throw new ArgumentException($"type not found: {typeFullName}");
                var methodToken = MetaDataUtils.FindMethodByName(mdi, typeToken, methodName, overloadIndex);
                if (methodToken == 0) throw new ArgumentException($"method not found: {typeFullName}::{methodName} (overload {overloadIndex})");

                var bp = Program.Session.DnDebugger.CreateBreakpoint(mod.DnModuleId, methodToken, 0, null);
                var entry = Program.Session.Breakpoints.Add(
                    "by_name",
                    $"{typeFullName}::{methodName} [token=0x{methodToken:X8}]",
                    bp);
                return Describe(entry);
            }));

        d.Register("bp.set_native",
            "[LIVE] Set a native-code breakpoint by absolute address. Params: {address:ulong, modulePath?:string, token?:uint}. If (modulePath,token) present, bp is scoped to that jitted function; else breakpoints the raw address via native code handle.",
            p => Program.Session.OnDbg(() =>
            {
                var address = Dispatcher.Req<ulong>(p, "address");
                var modulePath = Dispatcher.Opt<string?>(p, "modulePath", null);
                var token = Dispatcher.Opt<uint>(p, "token", 0);

                if (modulePath != null && token != 0)
                {
                    var mod = FindModule(modulePath) ?? throw new ArgumentException($"module not found: {modulePath}");
                    var moduleId = mod.DnModuleId;
                    var bp = Program.Session.DnDebugger.CreateNativeBreakpoint(moduleId, token, (uint)address, null);
                    var entry = Program.Session.Breakpoints.Add("native",
                        $"native {Path.GetFileName(mod.Name)}!0x{token:X8}+0x{address:X}", bp);
                    return Describe(entry);
                }
                throw new ArgumentException("bp.set_native currently requires (modulePath, token, offset)");
            }));

        d.Register("bp.list",
            "[LIVE] List all breakpoints registered on this agent (id, kind, description, enabled).",
            _ =>
            {
                var rows = new List<object>();
                foreach (var e in Program.Session.Breakpoints.All) rows.Add(Describe(e));
                return rows;
            });

        d.Register("bp.delete",
            "[LIVE] Remove a breakpoint by id. Params: {id:int}.",
            p => Program.Session.OnDbg(() =>
            {
                var id = Dispatcher.Req<int>(p, "id");
                if (!Program.Session.Breakpoints.TryGet(id, out var entry))
                    throw new ArgumentException($"bp id {id} not found");
                Program.Session.DnDebugger.RemoveBreakpoint((DnBreakpoint)entry.DnBreakpoint);
                Program.Session.Breakpoints.Remove(id);
                return new { deleted = id };
            }));

        d.Register("bp.enable",
            "[LIVE] Enable a breakpoint by id. Params: {id:int}.",
            p => Program.Session.OnDbg(() => SetEnabled(p, true)));

        d.Register("bp.disable",
            "[LIVE] Disable a breakpoint by id. Params: {id:int}.",
            p => Program.Session.OnDbg(() => SetEnabled(p, false)));
    }

    private static object SetEnabled(JObject? p, bool enabled)
    {
        var id = Dispatcher.Req<int>(p, "id");
        if (!Program.Session.Breakpoints.TryGet(id, out var entry))
            throw new ArgumentException($"bp id {id} not found");
        ((DnBreakpoint)entry.DnBreakpoint).IsEnabled = enabled;
        entry.Enabled = enabled;
        return Describe(entry);
    }

    private static object Describe(Services.BreakpointEntry e) => new
    {
        id = e.Id,
        kind = e.Kind,
        description = e.Description,
        enabled = e.Enabled,
    };

    private static DnModule? FindModule(string modulePathSuffix)
    {
        var dbg = Program.Session.DnDebugger;
        DnModule? best = null;
        foreach (var proc in dbg.Processes)
            foreach (var ad in proc.AppDomains)
                foreach (var asm in ad.Assemblies)
                    foreach (var mod in asm.Modules)
                    {
                        if (mod.Name.EndsWith(modulePathSuffix, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetFileName(mod.Name), modulePathSuffix, StringComparison.OrdinalIgnoreCase))
                            return mod;
                        if (best == null && mod.Name.IndexOf(modulePathSuffix, StringComparison.OrdinalIgnoreCase) >= 0)
                            best = mod;
                    }
        return best;
    }
}
