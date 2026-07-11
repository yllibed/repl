using System.Diagnostics.CodeAnalysis;

namespace Repl;

internal static class GlobalOptionParser
{
	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Global token scanning keeps precedence explicit so built-ins and custom options compose predictably.")]
	public static GlobalInvocationOptions Parse(
		IReadOnlyList<string> args,
		OutputOptions outputOptions,
		ParsingOptions parsingOptions)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(outputOptions);
		ArgumentNullException.ThrowIfNull(parsingOptions);

		var tokenComparer = parsingOptions.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;
		var remaining = new List<string>(args.Count);
		var remainingIndices = new List<int>(args.Count);
		var promptAnswers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var customGlobalValues = new Dictionary<string, List<string>>(tokenComparer);
		var diagnostics = new List<ParseDiagnostic>();
		var customTokenMap = BuildCustomTokenMap(parsingOptions.GlobalOptions, tokenComparer);
		var options = new GlobalInvocationOptions(remaining);
		var optionComparison = parsingOptions.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;

		for (var index = 0; index < args.Count; index++)
		{
			var argument = args[index];
			if (string.Equals(argument, "--help", optionComparison))
			{
				options = options with { HelpRequested = true };
				continue;
			}

			if (string.Equals(argument, "--interactive", optionComparison))
			{
				options = options with { InteractiveForced = true };
				continue;
			}

			if (string.Equals(argument, "--no-interactive", optionComparison))
			{
				options = options with { InteractivePrevented = true };
				continue;
			}

			if (string.Equals(argument, "--no-logo", optionComparison))
			{
				options = options with { LogoSuppressed = true };
				continue;
			}

			if (argument.StartsWith("--output:", optionComparison))
			{
				options = options with { OutputFormat = argument["--output:".Length..] };
				continue;
			}

			if (TryParseOutputAlias(argument, outputOptions, out var aliasFormat))
			{
				options = options with { OutputFormat = aliasFormat };
				continue;
			}

			if (TryParseResultFlowOption(
					args,
					ref index,
					argument,
					optionComparison,
					options.ResultFlow,
					outputOptions.ResultFlow.MaxPageSize,
					diagnostics,
					out var resultFlow))
			{
				options = options with { ResultFlow = resultFlow };
				continue;
			}

			if (TryParsePromptAnswer(argument, promptAnswers))
			{
				continue;
			}

			if (TryParseCustomGlobalOption(
				args,
				ref index,
				argument,
				customTokenMap,
				parsingOptions.GlobalOptions,
				customGlobalValues))
			{
				continue;
			}

			remaining.Add(argument);
			remainingIndices.Add(index);
		}

		var readonlyCustomGlobalValues = customGlobalValues.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyList<string>)pair.Value,
			StringComparer.OrdinalIgnoreCase);
		return options with
		{
			PromptAnswers = promptAnswers,
			CustomGlobalNamedOptions = readonlyCustomGlobalValues,
			Diagnostics = diagnostics,
			RemainingTokenIndices = remainingIndices,
		};
	}

	private static bool TryParseOutputAlias(
		string argument,
		OutputOptions outputOptions,
		out string format)
	{
		if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length <= 2)
		{
			format = string.Empty;
			return false;
		}

		return outputOptions.TryResolveAlias(argument[2..], out format!);
	}

	private static bool TryParsePromptAnswer(
		string argument,
		Dictionary<string, string> promptAnswers)
	{
		if (!argument.StartsWith("--answer:", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var answerToken = argument["--answer:".Length..];
		if (string.IsNullOrWhiteSpace(answerToken))
		{
			return true;
		}

		var separatorIndex = answerToken.IndexOf('=', StringComparison.Ordinal);
		if (separatorIndex < 0)
		{
			promptAnswers[answerToken] = "true";
			return true;
		}

		var name = answerToken[..separatorIndex];
		if (!string.IsNullOrWhiteSpace(name))
		{
			promptAnswers[name] = answerToken[(separatorIndex + 1)..];
		}

		return true;
	}

	private static bool TryParseResultFlowOption(
		IReadOnlyList<string> args,
		ref int index,
		string argument,
		StringComparison comparison,
		ResultFlowInvocationOptions current,
		int maxPageSize,
		List<ParseDiagnostic> diagnostics,
		out ResultFlowInvocationOptions resultFlow)
	{
		const string prefix = "--result:";
		resultFlow = current;
		if (!argument.StartsWith(prefix, comparison))
		{
			return false;
		}

		var token = argument[prefix.Length..];
		if (TrySplitToken(token, '=', out var name, out var inlineValue)
			|| TrySplitToken(token, ':', out name, out inlineValue))
		{
			return ApplyResultFlowOption(name, inlineValue, current, maxPageSize, diagnostics, out resultFlow);
		}

		if (string.Equals(token, "all", comparison))
		{
			resultFlow = current with { AllRequested = true };
			return true;
		}

		if (RequiresResultFlowValue(token, comparison)
			&& index + 1 < args.Count
			&& !args[index + 1].StartsWith('-'))
		{
			index++;
			return ApplyResultFlowOption(token, args[index], current, maxPageSize, diagnostics, out resultFlow);
		}

		if (RequiresResultFlowValue(token, comparison))
		{
			AddResultFlowDiagnostic(diagnostics, $"The result-flow option '--result:{token}' requires a value.");
			return true;
		}

		return ApplyResultFlowOption(token, "true", current, maxPageSize, diagnostics, out resultFlow);
	}

	private static bool ApplyResultFlowOption(
		string name,
		string value,
		ResultFlowInvocationOptions current,
		int maxPageSize,
		List<ParseDiagnostic> diagnostics,
		out ResultFlowInvocationOptions resultFlow)
	{
		resultFlow = current;
		if (string.Equals(name, "page-size", StringComparison.OrdinalIgnoreCase))
		{
			if (int.TryParse(
				value,
				System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture,
				out var pageSize))
			{
				resultFlow = current with { PageSize = ClampPageSize(pageSize, maxPageSize) };
			}

			return true;
		}

		if (string.Equals(name, "cursor", StringComparison.OrdinalIgnoreCase))
		{
			if (!ResultFlowCursorPolicy.TryValidate(value, out var error))
			{
				AddResultFlowDiagnostic(diagnostics, error);
				return true;
			}

			resultFlow = current with { Cursor = value };
			return true;
		}

		if (string.Equals(name, "pager", StringComparison.OrdinalIgnoreCase))
		{
			if (Enum.TryParse<ReplPagerMode>(value, ignoreCase: true, out var mode))
			{
				resultFlow = current with { PagerMode = mode };
			}

			return true;
		}

		if (string.Equals(name, "all", StringComparison.OrdinalIgnoreCase))
		{
			resultFlow = current with { AllRequested = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) };
			return true;
		}

		return false;
	}

	private static bool RequiresResultFlowValue(string token, StringComparison comparison) =>
		string.Equals(token, "page-size", comparison)
		|| string.Equals(token, "cursor", comparison)
		|| string.Equals(token, "pager", comparison);

	private static int ClampPageSize(int pageSize, int maxPageSize) =>
		Math.Clamp(pageSize, 1, Math.Max(1, maxPageSize));

	private static void AddResultFlowDiagnostic(List<ParseDiagnostic> diagnostics, string message) =>
		diagnostics.Add(new ParseDiagnostic(ParseDiagnosticSeverity.Error, message));

	// Resolves a custom-global token to the definition the parser would actually use — the
	// LAST registered definition wins a token/alias collision (BuildCustomTokenMap overwrites),
	// so callers must not scan definitions independently and pick a different one.
	internal static bool TryResolveCustomGlobalDefinition(
		string token,
		ParsingOptions parsingOptions,
		out GlobalOptionDefinition definition)
	{
		definition = null!;
		var comparer = parsingOptions.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;
		var tokenMap = BuildCustomTokenMap(parsingOptions.GlobalOptions, comparer);
		if (!TryResolveCustomGlobalName(token, tokenMap, out var optionName, out _))
		{
			return false;
		}

		return parsingOptions.GlobalOptions.TryGetValue(optionName, out definition!);
	}

	private static Dictionary<string, string> BuildCustomTokenMap(
		IReadOnlyDictionary<string, GlobalOptionDefinition> definitions,
		StringComparer comparer)
	{
		var tokenMap = new Dictionary<string, string>(comparer);
		foreach (var definition in definitions.Values)
		{
			tokenMap[definition.CanonicalToken] = definition.Name;
			foreach (var alias in definition.Aliases)
			{
				tokenMap[alias] = definition.Name;
			}
		}

		return tokenMap;
	}

	private static bool TryParseCustomGlobalOption(
		IReadOnlyList<string> args,
		ref int index,
		string argument,
		IReadOnlyDictionary<string, string> tokenMap,
		IReadOnlyDictionary<string, GlobalOptionDefinition> definitions,
		Dictionary<string, List<string>> customGlobalValues)
	{
		if (!TryResolveCustomGlobalName(argument, tokenMap, out var optionName, out var inlineValue))
		{
			return false;
		}

		var isBool = definitions.TryGetValue(optionName, out var def) && def.ValueType == typeof(bool);

		var value = inlineValue;
		if (value is null && !isBool
			&& index + 1 < args.Count
			&& (!args[index + 1].StartsWith('-') || IsSignedNumericLiteral(args[index + 1])))
		{
			index++;
			value = args[index];
		}

		if (!customGlobalValues.TryGetValue(optionName, out var values))
		{
			values = [];
			customGlobalValues[optionName] = values;
		}

		values.Add(value ?? "true");
		return true;
	}

	private static bool TryResolveCustomGlobalName(
		string argument,
		IReadOnlyDictionary<string, string> tokenMap,
		out string optionName,
		out string? inlineValue)
	{
		optionName = string.Empty;
		inlineValue = null;
		if (!argument.StartsWith('-'))
		{
			return false;
		}

		var optionToken = argument;
		if (TrySplitToken(argument, '=', out var namePart, out var valuePart)
			|| TrySplitToken(argument, ':', out namePart, out valuePart))
		{
			optionToken = namePart;
			inlineValue = valuePart;
		}

		if (!tokenMap.TryGetValue(optionToken, out optionName!))
		{
			return false;
		}

		return true;
	}

	private static bool IsSignedNumericLiteral(string token) =>
		InvocationOptionParser.IsSignedNumericLiteral(token);

	private static bool TrySplitToken(
		string token,
		char separator,
		out string namePart,
		out string valuePart)
	{
		var separatorIndex = token.IndexOf(separator, StringComparison.Ordinal);
		if (separatorIndex <= 0)
		{
			namePart = string.Empty;
			valuePart = string.Empty;
			return false;
		}

		namePart = token[..separatorIndex];
		valuePart = token[(separatorIndex + 1)..];
		return true;
	}
}
