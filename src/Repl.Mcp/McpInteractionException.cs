namespace Repl.Mcp;

/// <summary>
/// Thrown when an interactive prompt cannot be resolved in MCP mode.
/// </summary>
public sealed class McpInteractionException(string message) : InvalidOperationException(message);
