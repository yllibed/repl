namespace Repl.Mcp;

/// <summary>
/// Controls the text fallback emitted with structured MCP paged results.
/// </summary>
public enum McpPagedResultTextMode
{
	/// <summary>
	/// Emit compact serialized JSON in <c>Content</c> for MCP clients that ignore structured content.
	/// </summary>
	SerializedJson = 0,

	/// <summary>
	/// Emit only a short summary, minimizing token cost and keeping raw cursors out of text content.
	/// </summary>
	SummaryOnly = 1,

	/// <summary>
	/// Emit a short summary followed by compact serialized JSON.
	/// </summary>
	SummaryAndSerializedJson = 2,
}
