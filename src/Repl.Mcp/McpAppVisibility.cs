namespace Repl.Mcp;

/// <summary>
/// Controls whether an MCP App-linked tool is visible to the model, the app iframe, or both.
/// </summary>
[Flags]
public enum McpAppVisibility
{
	/// <summary>The tool is visible to the model.</summary>
	Model = 1,

	/// <summary>The tool is visible to the app iframe.</summary>
	App = 2,

	/// <summary>The tool is visible to both the model and the app iframe.</summary>
	ModelAndApp = Model | App,
}
