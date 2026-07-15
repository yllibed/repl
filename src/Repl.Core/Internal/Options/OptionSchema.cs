namespace Repl.Internal.Options;

internal sealed class OptionSchema
{
	public static OptionSchema Empty { get; } =
		new([], new Dictionary<string, OptionSchemaParameter>(StringComparer.OrdinalIgnoreCase));

	public OptionSchema(
		IReadOnlyList<OptionSchemaEntry> entries,
		IReadOnlyDictionary<string, OptionSchemaParameter> parameters)
	{
		Entries = entries;
		Parameters = parameters;
	}

	public IReadOnlyList<OptionSchemaEntry> Entries { get; }

	public IReadOnlyDictionary<string, OptionSchemaParameter> Parameters { get; }

	// Lazily materialized once: Entries is immutable and completion paths read this per
	// keystroke — recomputing the Distinct projection on every access was pure waste.
	// Benign race: concurrent first reads compute the same array.
	private string[]? _knownTokens;

	// Ordinal dedup: case-differing tokens can belong to DIFFERENT case-sensitive options
	// (per-entry overrides), so collapsing them ignoring case would drop a real token and
	// make "Did you mean" suggest the wrong casing.
	public IReadOnlyCollection<string> KnownTokens =>
		_knownTokens ??= [.. Entries.Select(entry => entry.Token).Distinct(StringComparer.Ordinal)];

	public IReadOnlyList<OptionSchemaEntry> ResolveToken(string token, ReplCaseSensitivity globalCaseSensitivity)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return [];
		}

		var matches = new List<OptionSchemaEntry>();
		foreach (var entry in Entries)
		{
			var effectiveCase = entry.CaseSensitivity ?? globalCaseSensitivity;
			var comparison = effectiveCase == ReplCaseSensitivity.CaseInsensitive
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;
			if (string.Equals(entry.Token, token, comparison))
			{
				matches.Add(entry);
			}
		}

		return matches;
	}

	public bool TryGetParameter(string parameterName, out OptionSchemaParameter parameter) =>
		Parameters.TryGetValue(parameterName, out parameter!);

	/// <summary>
	/// Effective arity of a parameter, resolved from its named-option/flag entry;
	/// parameters without such an entry default to the permissive <see cref="ReplArity.ZeroOrMore"/>.
	/// </summary>
	public ReplArity ResolveParameterArity(string parameterName) =>
		FindNamedEntry(parameterName)?.Arity ?? ReplArity.ZeroOrMore;

	/// <summary>
	/// Canonical display token of a parameter's named-option/flag entry (with prefix),
	/// or null for parameters without one (e.g. ArgumentOnly).
	/// </summary>
	public string? ResolveDisplayToken(string parameterName) =>
		FindNamedEntry(parameterName)?.Token;

	private OptionSchemaEntry? FindNamedEntry(string parameterName)
	{
		foreach (var entry in Entries)
		{
			if (string.Equals(entry.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase)
				&& entry.TokenKind is OptionSchemaTokenKind.NamedOption or OptionSchemaTokenKind.BoolFlag)
			{
				return entry;
			}
		}

		return null;
	}
}
