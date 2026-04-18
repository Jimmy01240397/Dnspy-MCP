using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DnSpyMcp.Services;

/// <summary>
/// Persistent TCP + newline-delimited-JSON client for dnspymcpagent.
/// One connection is kept alive across many tool invocations.
/// </summary>
public sealed class AgentClient : IDisposable
{
    private readonly object _connectLock = new();
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;
    private string _host = "127.0.0.1";
    private int _port = 5555;
    private string? _token;

    public bool IsConnected => _tcp?.Connected ?? false;
    public string? Host => _tcp != null ? _host : null;
    public int? Port => _tcp != null ? _port : null;

    public void Configure(string host, int port, string? token)
    {
        _host = host;
        _port = port;
        _token = token;
    }

    public void Connect()
    {
        lock (_connectLock)
        {
            CloseLocked();
            _tcp = new TcpClient();
            _tcp.Connect(_host, _port);
            _tcp.NoDelay = true;
            var stream = _tcp.GetStream();
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            var banner = _reader.ReadLine();
            if (banner == null) throw new IOException("agent closed without sending banner");

            if (_token != null)
            {
                var resp = CallLocked("auth", new { token = _token });
                if (resp["ok"]?.Value<bool>() != true)
                    throw new IOException("agent auth failed: " + resp["error"]);
            }
        }
    }

    public void Close()
    {
        lock (_connectLock) CloseLocked();
    }

    private void CloseLocked()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _tcp?.Close(); } catch { }
        _writer = null; _reader = null; _tcp = null;
    }

    public JObject Call(string method, object? @params = null)
    {
        lock (_connectLock)
        {
            if (_tcp == null || !_tcp.Connected)
                Connect();
            return CallLocked(method, @params);
        }
    }

    private JObject CallLocked(string method, object? @params)
    {
        if (_writer == null || _reader == null) throw new InvalidOperationException("not connected");
        int id = ++_nextId;
        var frame = JsonConvert.SerializeObject(new { id, method, @params });
        _writer.WriteLine(frame);
        var line = _reader.ReadLine() ?? throw new IOException("agent closed unexpectedly");
        return JObject.Parse(line);
    }

    public JToken Result(string method, object? @params = null)
    {
        var resp = Call(method, @params);
        if (resp["ok"]?.Value<bool>() != true)
            throw new InvalidOperationException($"agent error ({method}): {resp["error"]}");
        return resp["result"] ?? JValue.CreateNull();
    }

    public void Dispose() => Close();
}
