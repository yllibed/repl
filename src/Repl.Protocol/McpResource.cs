namespace Repl.Protocol;

/// <summary>
/// Machine-readable MCP resource descriptor.
/// </summary>
/// <param name="Uri">Resource URI.</param>
/// <param name="Name">Human-readable resource name.</param>
/// <param name="Description">Resource description.</param>
public sealed record McpResource(
	string Uri,
	string Name,
	string Description);
