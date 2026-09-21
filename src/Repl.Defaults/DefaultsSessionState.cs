using System.Collections.Concurrent;

namespace Repl;

internal sealed class DefaultsSessionState : IReplSessionState
{
	// Every concurrent Telnet, WebSocket and MCP session can resolve this state, so writes from
	// two sessions race. An unsynchronised Dictionary corrupts its bucket table under that.
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
