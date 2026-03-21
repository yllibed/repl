namespace Repl.Mcp;

/// <summary>
/// Controls the compatibility strategy used for dynamic MCP tool lists.
/// </summary>
public enum DynamicToolCompatibilityMode
{
	/// <summary>
	/// Exposes the real tool list directly and relies on standard MCP <c>list_changed</c> notifications.
	/// </summary>
	Disabled = 0,

	/// <summary>
	/// Exposes a bootstrap <c>discover_tools</c> / <c>call_tool</c> pair on the first <c>tools/list</c>
	/// response, then asks the client to refresh so it can see the real tool list.
	/// </summary>
	DiscoverAndCallShim = 1,
}
