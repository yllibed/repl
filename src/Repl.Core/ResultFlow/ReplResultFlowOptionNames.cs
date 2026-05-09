namespace Repl;

/// <summary>
/// Built-in result-flow option names reserved by Repl.
/// </summary>
public static class ReplResultFlowOptionNames
{
	/// <summary>
	/// CLI option used to continue from a cursor returned by a previous page.
	/// </summary>
	public const string Cursor = "--result:cursor";

	/// <summary>
	/// CLI option used to request a page size.
	/// </summary>
	public const string PageSize = "--result:page-size";

	/// <summary>
	/// CLI option used to request all rows when supported.
	/// </summary>
	public const string All = "--result:all";

	/// <summary>
	/// CLI option used to control the integrated human-output pager.
	/// </summary>
	public const string Pager = "--result:pager";

	/// <summary>
	/// MCP argument used to continue from a cursor returned by a previous page.
	/// </summary>
	public const string McpCursor = "_replCursor";

	/// <summary>
	/// MCP argument used to request a page size.
	/// </summary>
	public const string McpPageSize = "_replPageSize";
}
