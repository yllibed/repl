namespace Repl;

/// <summary>
/// In-memory history provider. Optionally accepts seed entries so
/// applications can pre-populate the history with example commands.
/// </summary>
public sealed class InMemoryHistoryProvider : IHistoryProvider
{
	private readonly List<string> _entries;

	/// <summary>
	/// Creates an empty history provider.
	/// </summary>
	public InMemoryHistoryProvider()
	{
		_entries = [];
	}

	/// <summary>
	/// Creates a history provider pre-populated with seed entries (oldest first).
	/// </summary>
	/// <param name="seedEntries">Initial history entries.</param>
	public InMemoryHistoryProvider(IEnumerable<string> seedEntries)
	{
		ArgumentNullException.ThrowIfNull(seedEntries);
		_entries = [.. seedEntries];
	}

	/// <inheritdoc />
	public ValueTask AddAsync(string entry, CancellationToken cancellationToken = default)
	{
		entry = string.IsNullOrWhiteSpace(entry)
			? throw new ArgumentException("History entry cannot be empty.", nameof(entry))
			: entry;
		cancellationToken.ThrowIfCancellationRequested();
		_entries.Add(entry);
		return ValueTask.CompletedTask;
	}

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<string>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default)
	{
		if (maxCount <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxCount), "Value must be greater than zero.");
		}

		cancellationToken.ThrowIfCancellationRequested();
		var skip = Math.Max(0, _entries.Count - maxCount);
		return ValueTask.FromResult<IReadOnlyList<string>>(_entries.Skip(skip).ToArray());
	}
}
