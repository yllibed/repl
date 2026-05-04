namespace Repl;

/// <summary>
/// Fetches pages of a result set on demand.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public interface IReplPageSource<T>
{
	/// <summary>
	/// Fetches a page for the supplied request.
	/// </summary>
	/// <param name="request">Page request.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The fetched page.</returns>
	ValueTask<ReplPage<T>> FetchAsync(
		ReplPageRequest request,
		CancellationToken cancellationToken = default);
}
