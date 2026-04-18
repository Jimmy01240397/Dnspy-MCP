using Newtonsoft.Json.Linq;

namespace DnSpyMcp.Agent.Services;

public sealed class JsonRpcReq
{
    public int Id { get; set; }
    public string Method { get; set; } = "";
    public JObject? Params { get; set; }
}

public sealed class JsonRpcResp
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public object? Result { get; set; }
    public string? Error { get; set; }
    public string? ErrorType { get; set; }
}
