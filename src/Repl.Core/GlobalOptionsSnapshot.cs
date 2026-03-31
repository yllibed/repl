namespace Repl;

internal sealed class GlobalOptionsSnapshot(ParsingOptions parsingOptions) : IGlobalOptionsAccessor
{
	private IReadOnlyDictionary<string, IReadOnlyList<string>> _sessionBaseline =
		new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

	private IReadOnlyDictionary<string, IReadOnlyList<string>> _currentValues =
		new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

	internal void SetSessionBaseline()
	{
		_sessionBaseline = _currentValues;
	}

	internal void Update(IReadOnlyDictionary<string, IReadOnlyList<string>> parsedValues)
	{
		var merged = new Dictionary<string, IReadOnlyList<string>>(_sessionBaseline, StringComparer.OrdinalIgnoreCase);
		foreach (var (key, value) in parsedValues)
		{
			merged[key] = value;
		}

		_currentValues = merged;
	}

	public T? GetValue<T>(string name, T? defaultValue = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		if (_currentValues.TryGetValue(name, out var values) && values.Count > 0)
		{
			return (T?)ParameterValueConverter.ConvertSingle(
				values[0],
				typeof(T),
				parsingOptions.NumericFormatProvider);
		}

		if (parsingOptions.GlobalOptions.TryGetValue(name, out var definition)
			&& definition.DefaultValue is not null)
		{
			return (T?)ParameterValueConverter.ConvertSingle(
				definition.DefaultValue,
				typeof(T),
				parsingOptions.NumericFormatProvider);
		}

		return defaultValue;
	}

	public IReadOnlyList<string> GetRawValues(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		return _currentValues.TryGetValue(name, out var values)
			? values
			: [];
	}

	public bool HasValue(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		return _currentValues.ContainsKey(name);
	}

	public IEnumerable<string> GetOptionNames() => _currentValues.Keys;
}
