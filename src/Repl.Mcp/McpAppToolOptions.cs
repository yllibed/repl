namespace Repl.Mcp;

/// <summary>
/// Metadata linking an MCP tool to an MCP App UI resource.
/// </summary>
/// <param name="ResourceUri">The <c>ui://</c> resource rendered for the tool.</param>
public sealed record McpAppToolOptions(string ResourceUri)
{
	/// <summary>
	/// Controls whether the linked tool is visible to the model, the app iframe, or both.
	/// </summary>
	public McpAppVisibility Visibility { get; init; } = McpAppVisibility.ModelAndApp;
}
