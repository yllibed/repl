namespace Repl.Protocol;

/// <summary>
/// Machine-readable MCP manifest contract.
/// </summary>
/// <param name="Name">Server/display name.</param>
/// <param name="Version">Manifest version.</param>
/// <param name="Tools">Available tools.</param>
/// <param name="Resources">Available resources.</param>
/// <param name="GeneratedAtUtc">Generation timestamp.</param>
public sealed record McpManifest(
	string Name,
	string Version,
	IReadOnlyList<McpTool> Tools,
	IReadOnlyList<McpResource> Resources,
	DateTimeOffset GeneratedAtUtc);
