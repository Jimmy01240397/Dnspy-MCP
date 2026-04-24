using System;
using System.Collections.Generic;
using System.IO;
using dndbg.Engine;
using DnSpyMcp.Agent.Services;

namespace DnSpyMcp.Agent.Handlers;

public static class ModuleHandlers
{
    public static void Register(Dispatcher d)
    {
        d.Register("module.list_live",
            "[DEBUG] Enumerate managed modules loaded in the attached process (via dndbg, real CLR view). Params: {verbose?:bool=false}. Default rows: {shortName, name, address}. verbose=true also returns {appDomain, assembly, size, isDynamic, isInMemory}.",
            p => Program.Session.OnDbg(() =>
            {
                bool verbose = Dispatcher.Opt<bool>(p, "verbose", false);
                var dbg = Program.Session.DnDebugger;
                var rows = new List<object>();
                foreach (var proc in dbg.Processes)
                    foreach (var ad in proc.AppDomains)
                        foreach (var asm in ad.Assemblies)
                            foreach (var mod in asm.Modules)
                            {
                                if (verbose)
                                {
                                    rows.Add(new
                                    {
                                        shortName = Path.GetFileName(mod.Name),
                                        name = mod.Name,
                                        address = (long)mod.Address,
                                        appDomain = ad.Name,
                                        assembly = asm.FullName,
                                        size = mod.Size,
                                        isDynamic = mod.IsDynamic,
                                        isInMemory = mod.IsInMemory,
                                    });
                                }
                                else
                                {
                                    rows.Add(new
                                    {
                                        shortName = Path.GetFileName(mod.Name),
                                        name = mod.Name,
                                        address = (long)mod.Address,
                                    });
                                }
                            }
                return rows;
            }));

        d.Register("module.find_type_live",
            "[DEBUG] Look up a type by full name across loaded modules. Returns module path + typeDef token(s). Params: {typeFullName:string}.",
            p => Program.Session.OnDbg(() =>
            {
                var typeFullName = Dispatcher.Req<string>(p, "typeFullName");
                var dbg = Program.Session.DnDebugger;
                var rows = new List<object>();
                foreach (var proc in dbg.Processes)
                    foreach (var ad in proc.AppDomains)
                        foreach (var asm in ad.Assemblies)
                            foreach (var mod in asm.Modules)
                            {
                                var mdi = mod.CorModule.GetMetaDataInterface<dndbg.COM.MetaData.IMetaDataImport>();
                                if (mdi == null) continue;
                                var token = MetaDataUtils.FindTypeDefByName(mdi, typeFullName);
                                if (token != 0)
                                    rows.Add(new { modulePath = mod.Name, typeDefToken = token });
                            }
                return rows;
            }));

        d.Register("module.list_type_methods",
            "[DEBUG] List all methods on a type. Params: {modulePath:string, typeFullName:string}. Returns [{token, name, attrs}].",
            p => Program.Session.OnDbg(() =>
            {
                var modulePath = Dispatcher.Req<string>(p, "modulePath");
                var typeFullName = Dispatcher.Req<string>(p, "typeFullName");
                var mod = FindModule(modulePath) ?? throw new ArgumentException($"module not found: {modulePath}");
                var mdi = mod.CorModule.GetMetaDataInterface<dndbg.COM.MetaData.IMetaDataImport>()
                    ?? throw new InvalidOperationException("no IMetaDataImport");
                var typeToken = MetaDataUtils.FindTypeDefByName(mdi, typeFullName);
                if (typeToken == 0) throw new ArgumentException($"type not found: {typeFullName}");
                var rows = new List<object>();
                foreach (var mt in MDAPI.GetMethodTokens(mdi, typeToken))
                {
                    var name = MDAPI.GetMethodName(mdi, mt) ?? "?";
                    MDAPI.GetMethodAttributes(mdi, mt, out var attr, out _);
                    rows.Add(new { token = mt, name, attributes = attr.ToString() });
                }
                return rows;
            }));
    }

    private static DnModule? FindModule(string modulePathSuffix)
    {
        var dbg = Program.Session.DnDebugger;
        foreach (var proc in dbg.Processes)
            foreach (var ad in proc.AppDomains)
                foreach (var asm in ad.Assemblies)
                    foreach (var mod in asm.Modules)
                    {
                        if (mod.Name.EndsWith(modulePathSuffix, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetFileName(mod.Name), modulePathSuffix, StringComparison.OrdinalIgnoreCase))
                            return mod;
                    }
        return null;
    }
}
