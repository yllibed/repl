namespace Repl;

internal sealed class DefaultsHistoryProvider : IHistoryProvider
{
	private readonly List<string> _entries = [];

	public ValueTask AddAsync(string entry, CancellationToken cancellationToken = default)
	{
		entry = string.IsNullOrWhiteSpace(entry)
			? throw new ArgumentException("History entry cannot be empty.", nameof(entry))
			: entry;
		cancellationToken.ThrowIfCancellationRequested();
		_entries.Add(entry);
		return ValueTask.CompletedTask;
	}

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
