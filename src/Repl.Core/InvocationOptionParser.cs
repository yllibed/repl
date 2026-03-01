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

		var diagnostics = new List<ParseDiagnostic>();
		var ignoreCase = options.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive;
		var tokenComparer = ignoreCase
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;
		var effectiveTokens = options.AllowResponseFiles
			? ExpandResponseFiles(tokens, diagnostics)
			: tokens;
		var namedOptions = new Dictionary<string, List<string>>(tokenComparer);
		var positionalArguments = new List<string>(tokens.Count);
		var knownOptions = knownOptionNames is null
			? null
			: new HashSet<string>(knownOptionNames, tokenComparer);
		var parseAsPositional = false;

		for (var index = 0; index < effectiveTokens.Count; index++)
		{
			var token = effectiveTokens[index];
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
			else if (index + 1 < effectiveTokens.Count
				&& !effectiveTokens[index + 1].StartsWith("--", StringComparison.Ordinal))
			{
				index++;
				optionValue = effectiveTokens[index];
			}

			if (knownOptions is not null
				&& !knownOptions.Contains(optionName)
				&& !options.AllowUnknownOptions)
			{
				var suggestion = TryResolveSuggestion(optionName, knownOptions, ignoreCase);
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
			tokenComparer);

		return new OptionParsingResult(readonlyNamedOptions, positionalArguments, diagnostics);
	}

	private static List<string> ExpandResponseFiles(
		IReadOnlyList<string> tokens,
		List<ParseDiagnostic> diagnostics)
	{
		var expanded = new List<string>(tokens.Count);
		foreach (var token in tokens)
		{
			if (!token.StartsWith('@') || token.Length == 1)
			{
				expanded.Add(token);
				continue;
			}

			var path = token[1..];
			if (!File.Exists(path))
			{
				diagnostics.Add(new ParseDiagnostic(
					ParseDiagnosticSeverity.Error,
					$"Response file '{path}' was not found.",
					Token: token));
				expanded.Add(token);
				continue;
			}

			var content = File.ReadAllText(path);
			var tokenization = ResponseFileTokenizer.Tokenize(content);
			expanded.AddRange(tokenization.Tokens);
			if (tokenization.HasTrailingEscape)
			{
				diagnostics.Add(new ParseDiagnostic(
					ParseDiagnosticSeverity.Warning,
					$"Response file '{path}' ends with a trailing escape character '\\'.",
					Token: token));
			}
		}

		return expanded;
	}

	private static string? TryResolveSuggestion(
		string optionName,
		IReadOnlyCollection<string> knownOptions,
		bool ignoreCase)
	{
		var bestDistance = int.MaxValue;
		string? bestMatch = null;
		foreach (var candidate in knownOptions)
		{
			var distance = ComputeLevenshteinDistance(
				optionName,
				candidate,
				ignoreCase);
			if (distance >= bestDistance)
			{
				continue;
			}

			bestDistance = distance;
			bestMatch = candidate;
		}

		return bestDistance <= 2 ? bestMatch : null;
	}

	private static int ComputeLevenshteinDistance(string source, string target, bool ignoreCase)
	{
		var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		if (string.Equals(source, target, comparison))
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
				var sourceChar = source[row - 1];
				var targetChar = target[column - 1];
				if (ignoreCase)
				{
					sourceChar = char.ToLowerInvariant(sourceChar);
					targetChar = char.ToLowerInvariant(targetChar);
				}

				var cost = sourceChar == targetChar ? 0 : 1;
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
