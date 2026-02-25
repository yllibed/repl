namespace Repl.Protocol;

/// <summary>
/// Machine-readable MCP tool descriptor.
/// </summary>
/// <param name="Name">Stable tool name.</param>
/// <param name="Description">Tool description.</param>
/// <param name="InputSchema">Input schema object for the tool.</param>
public sealed record McpTool(
	string Name,
	string Description,
	object InputSchema);
