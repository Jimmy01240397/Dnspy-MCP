using ModelContextProtocol;

namespace DnSpyMcp.Services;

/// <summary>
/// Catches any non-<see cref="McpException"/> thrown by a tool body and rethrows
/// it as <see cref="McpException"/>. The MCP C# SDK only forwards
/// <c>McpException.Message</c> to the client — anything else becomes a generic
/// "An error occurred invoking '&lt;tool&gt;'." with no reason, which leaves the
/// calling LLM unable to adjust.
/// </summary>
public static class ToolGuard
{
    public static T Run<T>(Func<T> fn)
    {
        try { return fn(); }
        catch (McpException) { throw; }
        catch (Exception ex)
        {
            throw new McpException($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
