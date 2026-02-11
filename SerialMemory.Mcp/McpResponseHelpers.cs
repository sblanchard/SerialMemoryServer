namespace SerialMemory.Mcp;

/// <summary>
/// Shared MCP protocol response helpers.
/// </summary>
internal static class McpResponseHelpers
{
    public static object CreateTextResponse(string text) => new
    {
        content = new[]
        {
            new { type = "text", text }
        }
    };

    public static object CreateErrorResponse(string message) => new
    {
        content = new[]
        {
            new { type = "text", text = $"Error: {message}" }
        },
        isError = true
    };
}
