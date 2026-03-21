namespace Repl.Mcp;

/// <summary>
/// Registration entry for an explicit MCP prompt.
/// </summary>
internal sealed record McpPromptRegistration(string Name, Delegate Handler);
