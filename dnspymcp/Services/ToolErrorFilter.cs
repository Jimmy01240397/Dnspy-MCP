using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DnSpyMcp.Services;

/// <summary>
/// Call-tool filter that catches every exception escaping the tool pipeline —
/// including MCP SDK parameter-binding failures (e.g. missing required param)
/// that fire before the tool body runs. Returns CallToolResult(IsError=true)
/// with the exception type + message so the calling LLM can correct itself,
/// instead of the framework's opaque "An error occurred invoking '&lt;tool&gt;'." fallback.
/// </summary>
public static class ToolErrorFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (request, ct) =>
        {
            try
            {
                return await next(request, ct);
            }
            catch (McpException ex)
            {
                return ErrorResult($"{ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                var tool = request.Params?.Name ?? "unknown";
                return ErrorResult($"{ex.GetType().Name} in '{tool}': {ex.Message}");
            }
        };

    private static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
