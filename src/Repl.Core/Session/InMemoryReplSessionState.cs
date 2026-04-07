namespace Repl;

internal sealed class InMemoryReplSessionState : IReplSessionState
{
	private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

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
		return _values.Remove(key);
	}

	public void Clear() => _values.Clear();
}
