using System.Diagnostics.CodeAnalysis;
using System.Globalization;

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
		Justification = "Legacy parser preserves prior permissive behavior for compatibility callers.")]
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

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Schema-aware parsing keeps token precedence and diagnostics explicit.")]
	public static OptionParsingResult Parse(
		IReadOnlyList<string> tokens,
		OptionSchema schema,
		ParsingOptions options)
	{
		ArgumentNullException.ThrowIfNull(tokens);
		ArgumentNullException.ThrowIfNull(schema);
		ArgumentNullException.ThrowIfNull(options);

		var diagnostics = new List<ParseDiagnostic>();
		var tokenComparer = options.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;
		var effectiveTokens = options.AllowResponseFiles
			? ExpandResponseFiles(tokens, diagnostics)
			: tokens;
		var namedOptions = new Dictionary<string, List<string>>(tokenComparer);
		var positionalArguments = new List<string>(tokens.Count);
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

			if (!LooksLikeOptionToken(token) || IsSignedNumericLiteral(token))
			{
				positionalArguments.Add(token);
				continue;
			}

			TrySplitOptionToken(token, out var optionToken, out var inlineValue);
			var matches = schema.ResolveToken(optionToken, options.OptionCaseSensitivity);
			var resolution = ResolveSingleSchemaEntry(matches);
			if (resolution.IsAmbiguous)
			{
				diagnostics.Add(new ParseDiagnostic(
					ParseDiagnosticSeverity.Error,
					$"Ambiguous option '{optionToken}'.",
					Token: token));
				continue;
			}

			if (resolution.Entry is null)
			{
				HandleUnknownOption(
					effectiveTokens,
					ref index,
					token,
					optionToken,
					inlineValue,
					schema,
					options,
					namedOptions,
					diagnostics);
				continue;
			}

			ApplyResolvedSchemaEntry(
				effectiveTokens,
				ref index,
				token,
				inlineValue,
				resolution.Entry,
				namedOptions,
				diagnostics);
		}

		ValidateArityAndConflicts(schema, namedOptions, diagnostics);
		var readonlyNamedOptions = namedOptions.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyList<string>)pair.Value,
			tokenComparer);
		return new OptionParsingResult(readonlyNamedOptions, positionalArguments, diagnostics);
	}

	private static bool LooksLikeOptionToken(string token) =>
		token.Length >= 2 && token[0] == '-';

	private static bool IsSignedNumericLiteral(string token)
	{
		if (token.Length < 2 || token[0] != '-')
		{
			return false;
		}

		return double.TryParse(
			token,
			NumberStyles.Float,
			CultureInfo.InvariantCulture,
			out _);
	}

	private static void TrySplitOptionToken(
		string token,
		out string optionToken,
		out string? inlineValue)
	{
		optionToken = token;
		inlineValue = null;
		var separatorIndex = token.IndexOfAny(['=', ':']);
		if (separatorIndex <= 0)
		{
			return;
		}

		optionToken = token[..separatorIndex];
		inlineValue = token[(separatorIndex + 1)..];
	}

	private static (OptionSchemaEntry? Entry, bool IsAmbiguous) ResolveSingleSchemaEntry(
		IReadOnlyList<OptionSchemaEntry> matches)
	{
		if (matches.Count == 0)
		{
			return (null, false);
		}

		var distinct = matches
			.DistinctBy(match => (match.ParameterName, match.TokenKind, match.InjectedValue), StringTupleComparer.Instance)
			.ToArray();
		return distinct.Length == 1
			? (distinct[0], false)
			: (null, true);
	}

	private static void HandleUnknownOption(
		IReadOnlyList<string> effectiveTokens,
		ref int index,
		string token,
		string optionToken,
		string? inlineValue,
		OptionSchema schema,
		ParsingOptions options,
		Dictionary<string, List<string>> namedOptions,
		List<ParseDiagnostic> diagnostics)
	{
		if (!options.AllowUnknownOptions)
		{
			var suggestion = TryResolveSuggestion(
				optionToken,
				schema.KnownTokens,
				options.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive);
			var message = suggestion is null
				? $"Unknown option '{optionToken}'."
				: $"Unknown option '{optionToken}'. Did you mean '{suggestion}'?";
			diagnostics.Add(new ParseDiagnostic(
				ParseDiagnosticSeverity.Error,
				message,
				Token: token,
				Suggestion: suggestion));
			return;
		}

		var optionName = TrimOptionPrefix(optionToken);
		var value = inlineValue;
		if (value is null
			&& index + 1 < effectiveTokens.Count
			&& ShouldConsumeFollowingTokenAsValue(effectiveTokens[index + 1]))
		{
			index++;
			value = effectiveTokens[index];
		}

		AddNamedValue(namedOptions, optionName, value ?? "true");
	}

	private static string TrimOptionPrefix(string token) =>
		token.StartsWith("--", StringComparison.Ordinal)
			? token[2..]
			: token[1..];

	private static bool ShouldConsumeFollowingTokenAsValue(string token)
	{
		if (string.Equals(token, "--", StringComparison.Ordinal))
		{
			return false;
		}

		return !LooksLikeOptionToken(token) || IsSignedNumericLiteral(token);
	}

	private static void ApplyResolvedSchemaEntry(
		IReadOnlyList<string> effectiveTokens,
		ref int index,
		string originalToken,
		string? inlineValue,
		OptionSchemaEntry entry,
		Dictionary<string, List<string>> namedOptions,
		List<ParseDiagnostic> diagnostics)
	{
		switch (entry.TokenKind)
		{
			case OptionSchemaTokenKind.NamedOption:
				ApplyNamedOptionValue(
					effectiveTokens,
					ref index,
					originalToken,
					inlineValue,
					entry,
					namedOptions,
					diagnostics);
				return;
			case OptionSchemaTokenKind.BoolFlag:
				ApplyBoolFlagValue(
					effectiveTokens,
					ref index,
					inlineValue,
					entry,
					namedOptions);
				return;
			case OptionSchemaTokenKind.ReverseFlag:
				if (inlineValue is not null)
				{
					diagnostics.Add(new ParseDiagnostic(
						ParseDiagnosticSeverity.Error,
						$"Option '{entry.Token}' does not accept an inline value.",
						Token: originalToken));
					return;
				}

				AddNamedValue(namedOptions, entry.ParameterName, "false");
				return;
			case OptionSchemaTokenKind.ValueAlias:
			case OptionSchemaTokenKind.EnumAlias:
				AddNamedValue(namedOptions, entry.ParameterName, entry.InjectedValue ?? "true");
				return;
			default:
				return;
		}
	}

	private static void ApplyNamedOptionValue(
		IReadOnlyList<string> effectiveTokens,
		ref int index,
		string originalToken,
		string? inlineValue,
		OptionSchemaEntry entry,
		Dictionary<string, List<string>> namedOptions,
		List<ParseDiagnostic> diagnostics)
	{
		if (inlineValue is not null)
		{
			AddNamedValue(namedOptions, entry.ParameterName, inlineValue);
			return;
		}

		if (index + 1 >= effectiveTokens.Count
			|| !ShouldConsumeFollowingTokenAsValue(effectiveTokens[index + 1]))
		{
			diagnostics.Add(new ParseDiagnostic(
				ParseDiagnosticSeverity.Error,
				$"Option '{entry.Token}' is missing a value.",
				Token: originalToken));
			return;
		}

		index++;
		AddNamedValue(namedOptions, entry.ParameterName, effectiveTokens[index]);
	}

	private static void ApplyBoolFlagValue(
		IReadOnlyList<string> effectiveTokens,
		ref int index,
		string? inlineValue,
		OptionSchemaEntry entry,
		Dictionary<string, List<string>> namedOptions)
	{
		if (inlineValue is not null)
		{
			AddNamedValue(namedOptions, entry.ParameterName, inlineValue);
			return;
		}

		if (index + 1 < effectiveTokens.Count
			&& ShouldConsumeFollowingTokenAsValue(effectiveTokens[index + 1]))
		{
			index++;
			AddNamedValue(namedOptions, entry.ParameterName, effectiveTokens[index]);
			return;
		}

		AddNamedValue(namedOptions, entry.ParameterName, "true");
	}

	private static void AddNamedValue(
		Dictionary<string, List<string>> namedOptions,
		string parameterName,
		string value)
	{
		if (!namedOptions.TryGetValue(parameterName, out var values))
		{
			values = [];
			namedOptions[parameterName] = values;
		}

		values.Add(value);
	}

	private static void ValidateArityAndConflicts(
		OptionSchema schema,
		Dictionary<string, List<string>> namedOptions,
		List<ParseDiagnostic> diagnostics)
	{
		foreach (var parameter in schema.Parameters.Values)
		{
			if (!namedOptions.TryGetValue(parameter.Name, out var values))
			{
				continue;
			}

			ValidateTooManyValues(schema, parameter, values, diagnostics);
			ValidateBooleanConflicts(parameter, values, diagnostics);
			ValidateEnumConflicts(parameter, values, diagnostics);
		}
	}

	private static void ValidateTooManyValues(
		OptionSchema schema,
		OptionSchemaParameter parameter,
		List<string> values,
		List<ParseDiagnostic> diagnostics)
	{
		var arity = ResolveParameterArity(schema, parameter.Name);
		if (arity == ReplArity.ZeroOrOne && values.Count > 1)
		{
			diagnostics.Add(new ParseDiagnostic(
				ParseDiagnosticSeverity.Error,
				$"Option '--{parameter.Name}' accepts at most one value."));
			return;
		}

		if (arity == ReplArity.ExactlyOne && values.Count != 1)
		{
			diagnostics.Add(new ParseDiagnostic(
				ParseDiagnosticSeverity.Error,
				$"Option '--{parameter.Name}' requires exactly one value."));
		}
	}

	private static void ValidateBooleanConflicts(
		OptionSchemaParameter parameter,
		List<string> values,
		List<ParseDiagnostic> diagnostics)
	{
		var effectiveType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
		if (effectiveType != typeof(bool) || values.Count <= 1)
		{
			return;
		}

		var hasTrue = values.Exists(value => bool.TryParse(value, out var parsed) && parsed);
		var hasFalse = values.Exists(value => bool.TryParse(value, out var parsed) && !parsed);
		if (!hasTrue || !hasFalse)
		{
			return;
		}

		diagnostics.Add(new ParseDiagnostic(
			ParseDiagnosticSeverity.Error,
			$"Option '--{parameter.Name}' cannot receive both positive and reverse values in the same invocation."));
	}

	private static void ValidateEnumConflicts(
		OptionSchemaParameter parameter,
		List<string> values,
		List<ParseDiagnostic> diagnostics)
	{
		var effectiveType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
		if (!effectiveType.IsEnum || values.Count <= 1)
		{
			return;
		}

		var comparer = parameter.CaseSensitivity == ReplCaseSensitivity.CaseSensitive
			? StringComparer.Ordinal
			: StringComparer.OrdinalIgnoreCase;
		if (!values.Distinct(comparer).Skip(1).Any())
		{
			return;
		}

		diagnostics.Add(new ParseDiagnostic(
			ParseDiagnosticSeverity.Error,
			$"Option '--{parameter.Name}' received multiple enum values in a single invocation."));
	}

	private static ReplArity ResolveParameterArity(OptionSchema schema, string parameterName)
	{
		var entry = schema.Entries.FirstOrDefault(candidate =>
			string.Equals(candidate.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase)
			&& candidate.TokenKind is OptionSchemaTokenKind.NamedOption or OptionSchemaTokenKind.BoolFlag);
		return entry?.Arity ?? ReplArity.ZeroOrMore;
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

	private sealed class StringTupleComparer : IEqualityComparer<(string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue)>
	{
		public static StringTupleComparer Instance { get; } = new();

		public bool Equals(
			(string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue) x,
			(string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue) y) =>
			string.Equals(x.ParameterName, y.ParameterName, StringComparison.OrdinalIgnoreCase)
			&& x.TokenKind == y.TokenKind
			&& string.Equals(x.InjectedValue, y.InjectedValue, StringComparison.Ordinal);

		public int GetHashCode((string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue) obj)
		{
			var parameterHash = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ParameterName);
			var injectedHash = obj.InjectedValue is null
				? 0
				: StringComparer.Ordinal.GetHashCode(obj.InjectedValue);
			return HashCode.Combine(parameterHash, (int)obj.TokenKind, injectedHash);
		}
	}
}
