namespace Repl;

internal sealed class GlobalOptionsSnapshot(ParsingOptions parsingOptions) : IGlobalOptionsAccessor
{
	private volatile IReadOnlyDictionary<string, IReadOnlyList<string>> _sessionBaseline =
		new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

	private volatile IReadOnlyDictionary<string, IReadOnlyList<string>> _currentValues =
		new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

	private volatile HashSet<string> _explicitKeys = new(StringComparer.OrdinalIgnoreCase);

	internal void SetSessionBaseline()
	{
		// Capture only the explicitly parsed values as the new baseline.
		// This prevents stale baselines from leaking across separate Run() calls.
		var baseline = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var key in _explicitKeys)
		{
			if (_currentValues.TryGetValue(key, out var values))
			{
				baseline[key] = values;
			}
		}

		_sessionBaseline = baseline;
		_currentValues = baseline;
	}

	/// <param name="parsedValues">The globals this invocation carried on its own tokens.</param>
	/// <param name="preserveSessionExplicitKeys">
	/// Whether the session's own globals count as explicitly provided. A sub-invocation carries only
	/// its own tokens, but the session's values stay in effect — they are merged in below.
	/// Explicitness has to travel with them, or <see cref="HasValue"/> denies an option whose value
	/// <see cref="GetValue{T}"/> still returns, and a module presence predicate reading it decides
	/// differently depending on which invocation ran last. A top-level run passes
	/// <see langword="false"/>: it is about to become the baseline itself, and carrying the previous
	/// one's keys into <see cref="SetSessionBaseline"/> is the leak that method exists to prevent.
	/// The interactive resolver is the third caller and also passes <see langword="false"/>: each
	/// committed line is a fresh invocation, so a baseline-only key is in force without having been
	/// provided on it — which is what <see cref="HasValue"/> reports.
	/// </param>
	internal void Update(
		IReadOnlyDictionary<string, IReadOnlyList<string>> parsedValues,
		bool preserveSessionExplicitKeys = false)
	{
		var baseline = _sessionBaseline;
		var explicitKeys = new HashSet<string>(parsedValues.Keys, StringComparer.OrdinalIgnoreCase);
		if (preserveSessionExplicitKeys)
		{
			foreach (var key in baseline.Keys)
			{
				explicitKeys.Add(key);
			}
		}

		_explicitKeys = explicitKeys;
		var merged = new Dictionary<string, IReadOnlyList<string>>(baseline, StringComparer.OrdinalIgnoreCase);
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

		return _explicitKeys.Contains(name);
	}

	public IEnumerable<string> GetOptionNames() => _currentValues.Keys;
}
