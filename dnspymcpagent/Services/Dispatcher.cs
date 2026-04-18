using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DnSpyMcp.Agent.Services;

/// <summary>
/// Routes a JSON-RPC-ish request (`{"id","method","params"}`) to a registered handler.
/// Method names use dot-notation ("session.attach", "bp.set_il", ...).
/// </summary>
public sealed class Dispatcher
{
    private readonly Dictionary<string, Func<JObject?, object?>> _routes = new();
    private readonly Dictionary<string, string> _descriptions = new();

    public void Register(string method, string description, Func<JObject?, object?> handler)
    {
        _routes[method] = handler;
        _descriptions[method] = description;
    }

    public IReadOnlyDictionary<string, string> Descriptions => _descriptions;

    public JsonRpcResp Dispatch(JsonRpcReq req)
    {
        if (req.Method == "__list__")
        {
            var rows = new List<object>();
            foreach (var kv in _descriptions) rows.Add(new { method = kv.Key, description = kv.Value });
            return new JsonRpcResp { Id = req.Id, Ok = true, Result = rows };
        }

        if (!_routes.TryGetValue(req.Method, out var fn))
        {
            return new JsonRpcResp
            {
                Id = req.Id,
                Ok = false,
                Error = $"unknown method: {req.Method}",
                ErrorType = "unknown_method",
            };
        }

        try
        {
            var result = fn(req.Params);
            return new JsonRpcResp { Id = req.Id, Ok = true, Result = result };
        }
        catch (Exception ex)
        {
            return new JsonRpcResp
            {
                Id = req.Id,
                Ok = false,
                Error = ex.Message,
                ErrorType = ex.GetType().Name,
            };
        }
    }

    public static T Req<T>(JObject? p, string name)
    {
        if (p is null || !p.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var tok))
            throw new ArgumentException($"missing required param: {name}");
        return tok.ToObject<T>() ?? throw new ArgumentException($"param {name} is null");
    }

    public static T Opt<T>(JObject? p, string name, T fallback)
    {
        if (p is null || !p.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var tok)) return fallback;
        if (tok.Type == JTokenType.Null) return fallback;
        return tok.ToObject<T>() ?? fallback;
    }
}
