namespace Repl;

/// <summary>
/// Fetches result-flow pages on demand.
/// </summary>
public interface IReplPageSource
{
	/// <summary>
	/// Fetches a page for the supplied request.
	/// </summary>
	/// <param name="request">Page request.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The fetched page.</returns>
	ValueTask<IReplPage> FetchPageAsync(
		ReplPageRequest request,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches pages of a result set on demand.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public interface IReplPageSource<T> : IReplPageSource
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

	async ValueTask<IReplPage> IReplPageSource.FetchPageAsync(
		ReplPageRequest request,
		CancellationToken cancellationToken)
	{
		return await FetchAsync(request, cancellationToken).ConfigureAwait(false);
	}
}
