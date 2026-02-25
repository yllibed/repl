namespace Repl;

/// <summary>
/// Provides command history storage for interactive sessions.
/// </summary>
public interface IHistoryProvider
{
	/// <summary>
	/// Persists one interactive command line.
	/// </summary>
	/// <param name="entry">Raw command line as typed by the user.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>An awaitable operation.</returns>
	ValueTask AddAsync(string entry, CancellationToken cancellationToken = default);

	/// <summary>
	/// Reads the most recent history entries.
	/// </summary>
	/// <param name="maxCount">Maximum number of entries to return.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Most recent entries ordered from oldest to newest within the returned window.</returns>
	ValueTask<IReadOnlyList<string>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default);
}
