namespace Repl;

/// <summary>
/// Metadata describing one page of a result set.
/// </summary>
/// <param name="Cursor">Cursor used to fetch the current page.</param>
/// <param name="NextCursor">Cursor that fetches the next page, when available.</param>
/// <param name="TotalCount">Total result count, when known.</param>
/// <param name="PageSize">Requested or effective page size.</param>
public sealed record ReplPageInfo(
	string? Cursor,
	string? NextCursor,
	long? TotalCount,
	int PageSize)
{
	/// <summary>
	/// Gets a value indicating whether another page is available.
	/// </summary>
	public bool HasMore => !string.IsNullOrWhiteSpace(NextCursor);
}
