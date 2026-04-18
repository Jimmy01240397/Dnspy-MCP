using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace DnSpyMcp.Agent.Services;

/// <summary>
/// Persistent TCP + newline-delimited-JSON server.
/// Exactly one client connection is honored at a time — the second connection attempt
/// while one is active is rejected with a banner line and closed.
/// The expected client lifecycle:
///   1) Client opens TCP connection once per debugger session.
///   2) Client sends many <c>JsonRpcReq</c> (one per line) and reads <c>JsonRpcResp</c> (one per line).
///   3) Client closes the connection when the debugger session ends.
/// </summary>
public sealed class TcpJsonServer
{
    private readonly Dispatcher _dispatcher;
    private readonly string _host;
    private readonly int _port;
    private readonly string? _token;
    private TcpListener? _listener;
    private int _clientActive;

    public TcpJsonServer(Dispatcher dispatcher, string host, int port, string? token)
    {
        _dispatcher = dispatcher;
        _host = host;
        _port = port;
        _token = token;
    }

    public void Start()
    {
        var ip = _host == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(_host);
        _listener = new TcpListener(ip, _port);
        _listener.Start();
    }

    public void RunAcceptLoop(CancellationToken ct)
    {
        if (_listener is null) throw new InvalidOperationException("listener not started");
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
        }
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { /* ignore */ }
    }

    private void HandleClient(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";

        if (Interlocked.CompareExchange(ref _clientActive, 1, 0) != 0)
        {
            TryWriteBannerAndClose(client, new { ok = false, error = "another client already connected" });
            Console.WriteLine($"[tcp] reject {remote}: busy");
            return;
        }

        Console.WriteLine($"[tcp] accept {remote}");
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };

            var banner = JsonConvert.SerializeObject(new
            {
                ok = true,
                banner = "dnspymcpagent",
                version = "0.1.0",
                requiresToken = _token != null,
            });
            writer.WriteLine(banner);

            bool authed = _token == null;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;

                JsonRpcReq? req;
                try { req = JsonConvert.DeserializeObject<JsonRpcReq>(line); }
                catch (Exception ex)
                {
                    writer.WriteLine(JsonConvert.SerializeObject(new JsonRpcResp
                    {
                        Id = 0, Ok = false, Error = $"bad json: {ex.Message}", ErrorType = "parse_error",
                    }));
                    continue;
                }
                if (req is null) continue;

                if (!authed)
                {
                    if (req.Method != "auth")
                    {
                        writer.WriteLine(JsonConvert.SerializeObject(new JsonRpcResp
                        {
                            Id = req.Id, Ok = false, Error = "auth required", ErrorType = "auth_required",
                        }));
                        continue;
                    }
                    var got = Dispatcher.Opt<string>(req.Params, "token", "");
                    if (got != _token)
                    {
                        writer.WriteLine(JsonConvert.SerializeObject(new JsonRpcResp
                        {
                            Id = req.Id, Ok = false, Error = "wrong token", ErrorType = "auth_failed",
                        }));
                        continue;
                    }
                    authed = true;
                    writer.WriteLine(JsonConvert.SerializeObject(new JsonRpcResp
                    {
                        Id = req.Id, Ok = true, Result = new { authed = true },
                    }));
                    continue;
                }

                var resp = _dispatcher.Dispatch(req);
                writer.WriteLine(JsonConvert.SerializeObject(resp, _jsonSettings));
            }
        }
        catch (IOException) { /* peer dropped */ }
        catch (Exception ex) { Console.Error.WriteLine($"[tcp] client error: {ex.Message}"); }
        finally
        {
            try { client.Close(); } catch { }
            Interlocked.Exchange(ref _clientActive, 0);
            Console.WriteLine($"[tcp] close  {remote}");
        }
    }

    private static readonly JsonSerializerSettings _jsonSettings = new()
    {
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
    };

    private static void TryWriteBannerAndClose(TcpClient client, object payload)
    {
        try
        {
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            writer.WriteLine(JsonConvert.SerializeObject(payload));
        }
        catch { /* ignore */ }
        finally { try { client.Close(); } catch { } }
    }
}
