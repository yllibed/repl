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

	/// <summary>
	/// Creates a paged result from an already fetched page.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <param name="items">Items in the current page.</param>
	/// <param name="nextCursor">Cursor for the next page, when one exists.</param>
	/// <param name="totalCount">Total item count, when known without expensive enumeration.</param>
	/// <returns>A result page consumable by Repl renderers.</returns>
	ReplPage<T> Page<T>(
		IReadOnlyList<T> items,
		string? nextCursor = null,
		long? totalCount = null);

	/// <summary>
	/// Creates a lazy page source that can fetch additional pages on demand.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <param name="fetch">Page fetch delegate.</param>
	/// <returns>A page source consumable by interactive renderers.</returns>
	IReplPageSource<T> CreateSource<T>(
		Func<ReplPageRequest, CancellationToken, ValueTask<ReplPage<T>>> fetch);
}
