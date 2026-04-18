using DnSpyMcp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DnSpyMcp;

internal static class Program
{
    private static int Main(string[] args)
    {
        var cli = Cli.Parse(args);
        if (cli is null) return 1;

        return cli.Transport switch
        {
            "stdio" => RunStdio(cli),
            "http"  => RunHttp(cli),
            "sse"   => RunSse(cli),
            _       => Fail($"unknown transport: {cli.Transport}"),
        };
    }

    private static int Fail(string msg) { Console.Error.WriteLine(msg); return 1; }

    // ---------- stdio (default) ----------
    private static int RunStdio(Cli cli)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        RegisterShared(builder.Services, cli);
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
        builder.Build().Run();
        return 0;
    }

    // ---------- http (Streamable HTTP) ----------
    private static int RunHttp(Cli cli)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        RegisterShared(builder.Services, cli);
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();
        var app = builder.Build();
        app.MapMcp(cli.McpPath);
        app.Urls.Add($"http://{cli.BindHost}:{cli.BindPort}");
        Console.Error.WriteLine($"dnspymcp listening on http://{cli.BindHost}:{cli.BindPort}{cli.McpPath}");
        app.Run();
        return 0;
    }

    // ---------- sse (legacy /sse transport; same pipeline as http) ----------
    private static int RunSse(Cli cli)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        RegisterShared(builder.Services, cli);
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => { })
            .WithToolsFromAssembly();
        var app = builder.Build();
        app.MapMcp(cli.McpPath);
        app.Urls.Add($"http://{cli.BindHost}:{cli.BindPort}");
        Console.Error.WriteLine($"dnspymcp (sse) listening on http://{cli.BindHost}:{cli.BindPort}{cli.McpPath}");
        app.Run();
        return 0;
    }

    private static void RegisterShared(IServiceCollection s, Cli cli)
    {
        var registry = new AgentRegistry();
        if (cli.AgentHost != null) registry.Default.Configure(cli.AgentHost, cli.AgentPort, cli.AgentToken);
        s.AddSingleton(registry);
        s.AddSingleton<AgentClient>(registry.Default);
        s.AddSingleton<Workspace>();
    }
}

internal sealed class Cli
{
    public string Transport { get; set; } = "stdio";
    public string BindHost { get; set; } = "127.0.0.1";
    public int BindPort { get; set; } = 5556;
    public string McpPath { get; set; } = "/mcp";

    public string? AgentHost { get; set; }
    public int AgentPort { get; set; } = 5555;
    public string? AgentToken { get; set; }

    public static Cli? Parse(string[] args)
    {
        var o = new Cli();
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--transport": o.Transport = Next() ?? throw new ArgumentException("--transport needs value"); break;
                case "--bind-host": o.BindHost = Next() ?? throw new ArgumentException("--bind-host needs value"); break;
                case "--bind-port": o.BindPort = int.Parse(Next()!); break;
                case "--mcp-path":  o.McpPath = Next() ?? "/mcp"; break;
                case "--agent-host": o.AgentHost = Next(); break;
                case "--agent-port": o.AgentPort = int.Parse(Next()!); break;
                case "--agent-token": o.AgentToken = Next(); break;
                case "--help":
                case "-?":
                case "-h":
                    PrintHelp(); return null;
                default:
                    Console.Error.WriteLine($"unknown arg: {args[i]}");
                    PrintHelp(); return null;
            }
        }
        return o;
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine("""
            dnspymcp — MCP server exposing dnSpy static & ICorDebug live tools.

            Transports (pick one via --transport):
              stdio        Default. One MCP session over stdin/stdout.
              http         Streamable-HTTP transport (modern MCP).
              sse          Legacy SSE transport (same endpoint; older clients).

            Usage:
              dnspymcp [--transport stdio|http|sse]
                       [--bind-host 127.0.0.1] [--bind-port 5556] [--mcp-path /mcp]
                       [--agent-host HOST] [--agent-port 5555] [--agent-token TOK]

            --agent-* presets the TCP connection target for the live_* tools.
            You can always call live_agent_connect to override at runtime.
            """);
    }
}
