namespace Repl;

/// <summary>
/// Convenience helpers for creating result-flow pages from page-source requests.
/// </summary>
public static class ReplPageRequestExtensions
{
	/// <summary>
	/// Creates a typed result page for the supplied request.
	/// </summary>
	/// <typeparam name="T">Item type.</typeparam>
	/// <param name="request">The page-source request being handled.</param>
	/// <param name="items">Items in the current page.</param>
	/// <param name="nextCursor">Cursor for the next page, when one exists.</param>
	/// <param name="totalCount">Total item count, when known without expensive enumeration.</param>
	/// <returns>A result page consumable by Repl renderers.</returns>
	public static ReplPage<T> Page<T>(
		this ReplPageRequest request,
		IReadOnlyList<T> items,
		string? nextCursor = null,
		long? totalCount = null)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(items);

		return new ReplPage<T>(
			items,
			new ReplPageInfo(
				Cursor: request.Cursor,
				NextCursor: nextCursor,
				TotalCount: totalCount,
				PageSize: request.PageSize));
	}
}
