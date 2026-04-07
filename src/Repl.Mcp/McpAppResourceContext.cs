namespace Repl.Mcp;

/// <summary>
/// Request context passed to MCP App UI resource factories.
/// </summary>
/// <param name="Uri">The requested UI resource URI.</param>
public sealed record McpAppResourceContext(string Uri);
