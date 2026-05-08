namespace Repl;

/// <summary>
/// Provides paging intent and output-capacity hints to command handlers.
/// </summary>
/// <remarks>
/// Handlers can use this context to avoid loading or returning unbounded result sets.
/// The visible-row hint is best-effort: terminal, hosted, and MCP surfaces can expose
/// different capacities, and redirected output usually has no visible screen.
/// </remarks>
public interface IReplPagingContext
{
	/// <summary>
	/// Gets a best-effort hint for the number of data rows the current output surface can show.
	/// </summary>
	int? VisibleRowCapacityHint { get; }

	/// <summary>
	/// Gets the page size suggested for the current invocation.
	/// </summary>
	int SuggestedPageSize { get; }

	/// <summary>
	/// Gets the maximum page size allowed by the current application configuration.
	/// </summary>
	int MaxPageSize { get; }

	/// <summary>
	/// Gets the opaque cursor supplied by the caller, when continuing a paged result.
	/// </summary>
	string? Cursor { get; }

	/// <summary>
	/// Gets a value indicating whether the caller explicitly requested all available rows.
	/// </summary>
	bool AllRequested { get; }

	/// <summary>
	/// Gets the kind of output surface driving this invocation.
	/// </summary>
	ReplResultSurface Surface { get; }

}
