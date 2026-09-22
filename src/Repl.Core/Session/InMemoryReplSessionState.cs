using System.Collections.Concurrent;

namespace Repl;

internal sealed class InMemoryReplSessionState : IReplSessionState
{
	// Registered Scoped, so sessions no longer share an instance — but concurrent calls WITHIN one
	// session still do: several MCP tool calls run at once on one connection, and a singleton that
	// captured a scope hands its instance to every later caller. An unsynchronised Dictionary does
	// not merely interleave under that, it corrupts its bucket table.
	private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

	public bool TryGet<T>(string key, out T? value)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(key);

		if (_values.TryGetValue(key, out var existing) && existing is T typed)
		{
			value = typed;
			return true;
		}

		value = default;
		return false;
	}

	public T? Get<T>(string key)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(key);
		return TryGet<T>(key, out var value) ? value : default;
	}

	public void Set<T>(string key, T value)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(key);
		_values[key] = value;
	}

	public bool Remove(string key)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(key);
		return _values.TryRemove(key, out _);
	}

	public void Clear() => _values.Clear();
}
