namespace Repl;

/// <summary>
/// Describes an MCP client root.
/// </summary>
/// <param name="Uri">Root URI.</param>
/// <param name="Name">Optional display name.</param>
public sealed record McpClientRoot(Uri Uri, string? Name = null);
