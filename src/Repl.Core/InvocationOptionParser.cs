namespace Repl;

internal static class InvocationOptionParser
{
	public static OptionParsingResult Parse(IReadOnlyList<string> tokens)
	{
		ArgumentNullException.ThrowIfNull(tokens);

		var namedOptions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		var positionalArguments = new List<string>(tokens.Count);
		var parseAsPositional = false;

		for (var index = 0; index < tokens.Count; index++)
		{
			var token = tokens[index];
			if (parseAsPositional)
			{
				positionalArguments.Add(token);
				continue;
			}

			if (string.Equals(token, "--", StringComparison.Ordinal))
			{
				parseAsPositional = true;
				continue;
			}

			if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length <= 2)
			{
				positionalArguments.Add(token);
				continue;
			}

			var optionName = token[2..];
			string? optionValue = null;
			if (optionName.Contains('=', StringComparison.Ordinal))
			{
				var parts = optionName.Split('=', 2, StringSplitOptions.TrimEntries);
				optionName = parts[0];
				optionValue = parts[1];
			}
			else if (index + 1 < tokens.Count
				&& !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
			{
				index++;
				optionValue = tokens[index];
			}

			if (!namedOptions.TryGetValue(optionName, out var values))
			{
				values = [];
				namedOptions[optionName] = values;
			}

			values.Add(optionValue ?? "true");
		}

		var readonlyNamedOptions = namedOptions.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyList<string>)pair.Value,
			StringComparer.OrdinalIgnoreCase);

		return new OptionParsingResult(readonlyNamedOptions, positionalArguments);
	}
}
