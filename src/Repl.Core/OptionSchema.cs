namespace Repl;

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

	public IReadOnlyCollection<string> KnownTokens =>
		Entries.Select(entry => entry.Token).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

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
}
