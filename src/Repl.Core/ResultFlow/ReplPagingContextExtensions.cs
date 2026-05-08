namespace Repl;

/// <summary>
/// Convenience helpers for creating result-flow pages from <see cref="IReplPagingContext"/>.
/// </summary>
public static class ReplPagingContextExtensions
{
	/// <summary>
	/// Creates a paged result from an already fetched page.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <param name="context">Paging context for the current invocation.</param>
	/// <param name="items">Items in the current page.</param>
	/// <param name="nextCursor">Cursor for the next page, when one exists.</param>
	/// <param name="totalCount">Total item count, when known without expensive enumeration.</param>
	/// <returns>A result page consumable by Repl renderers.</returns>
	public static ReplPage<T> Page<T>(
		this IReplPagingContext context,
		IReadOnlyList<T> items,
		string? nextCursor = null,
		long? totalCount = null)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(items);
		var pageInfo = new ReplPageInfo(
			context.Cursor,
			nextCursor,
			totalCount,
			context.SuggestedPageSize);
		return new ReplPage<T>(items, pageInfo);
	}

	/// <summary>
	/// Creates a lazy page source that can fetch additional pages on demand.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <param name="context">Paging context for the current invocation.</param>
	/// <param name="fetch">Page fetch delegate.</param>
	/// <returns>A page source consumable by interactive renderers.</returns>
	public static IReplPageSource<T> CreateSource<T>(
		this IReplPagingContext context,
		Func<ReplPageRequest, CancellationToken, ValueTask<ReplPage<T>>> fetch)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(fetch);
		return ReplPageSource.Create(fetch);
	}
}
