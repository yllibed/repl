using System.Diagnostics.CodeAnalysis;

namespace Repl;

internal static class InvocationOptionParser
{
	public static OptionParsingResult Parse(IReadOnlyList<string> tokens)
	{
		ArgumentNullException.ThrowIfNull(tokens);
		return Parse(
			tokens,
			new ParsingOptions
			{
				AllowUnknownOptions = true,
			},
			knownOptionNames: null);
	}

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Option token scanning keeps ordering/precedence explicit for parser correctness.")]
	public static OptionParsingResult Parse(
		IReadOnlyList<string> tokens,
		ParsingOptions options,
		IReadOnlyCollection<string>? knownOptionNames)
	{
		ArgumentNullException.ThrowIfNull(tokens);
		ArgumentNullException.ThrowIfNull(options);

		var namedOptions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		var positionalArguments = new List<string>(tokens.Count);
		var diagnostics = new List<ParseDiagnostic>();
		var knownOptions = knownOptionNames is null
			? null
			: new HashSet<string>(knownOptionNames, StringComparer.OrdinalIgnoreCase);
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
			else if (optionName.Contains(':', StringComparison.Ordinal))
			{
				var parts = optionName.Split(':', 2, StringSplitOptions.TrimEntries);
				optionName = parts[0];
				optionValue = parts[1];
			}
			else if (index + 1 < tokens.Count
				&& !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
			{
				index++;
				optionValue = tokens[index];
			}

			var suggestion = knownOptions is null
				? null
				: TryResolveSuggestion(optionName, knownOptions);
			if (knownOptions is not null
				&& !knownOptions.Contains(optionName)
				&& !options.AllowUnknownOptions)
			{
				var message = suggestion is null
					? $"Unknown option '--{optionName}'."
					: $"Unknown option '--{optionName}'. Did you mean '--{suggestion}'?";
				diagnostics.Add(new ParseDiagnostic(
					ParseDiagnosticSeverity.Error,
					message,
					Token: token,
					Suggestion: suggestion is null ? null : $"--{suggestion}"));
				continue;
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

		return new OptionParsingResult(readonlyNamedOptions, positionalArguments, diagnostics);
	}

	private static string? TryResolveSuggestion(string optionName, IReadOnlyCollection<string> knownOptions)
	{
		var bestDistance = int.MaxValue;
		string? bestMatch = null;
		foreach (var candidate in knownOptions)
		{
			var distance = ComputeLevenshteinDistance(optionName, candidate);
			if (distance >= bestDistance)
			{
				continue;
			}

			bestDistance = distance;
			bestMatch = candidate;
		}

		return bestDistance <= 2 ? bestMatch : null;
	}

	private static int ComputeLevenshteinDistance(string source, string target)
	{
		if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}

		if (source.Length == 0)
		{
			return target.Length;
		}

		if (target.Length == 0)
		{
			return source.Length;
		}

		if (target.Length > source.Length)
		{
			(source, target) = (target, source);
		}

		var previous = new int[target.Length + 1];
		var current = new int[target.Length + 1];
		for (var column = 0; column <= target.Length; column++)
		{
			previous[column] = column;
		}

		for (var row = 1; row <= source.Length; row++)
		{
			current[0] = row;
			for (var column = 1; column <= target.Length; column++)
			{
				var cost = char.ToLowerInvariant(source[row - 1]) == char.ToLowerInvariant(target[column - 1]) ? 0 : 1;
				var deletion = previous[column] + 1;
				var insertion = current[column - 1] + 1;
				var substitution = previous[column - 1] + cost;
				current[column] = Math.Min(Math.Min(deletion, insertion), substitution);
			}

			(previous, current) = (current, previous);
		}

		return previous[target.Length];
	}
}
