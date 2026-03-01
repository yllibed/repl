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
		var promptAnswers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var customGlobalValues = new Dictionary<string, List<string>>(tokenComparer);
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

			if (TryParsePromptAnswer(argument, promptAnswers))
			{
				continue;
			}

			if (TryParseCustomGlobalOption(
				args,
				ref index,
				argument,
				customTokenMap,
				customGlobalValues))
			{
				continue;
			}

			remaining.Add(argument);
		}

		var readonlyCustomGlobalValues = customGlobalValues.ToDictionary(
			pair => pair.Key,
			pair => (IReadOnlyList<string>)pair.Value,
			StringComparer.OrdinalIgnoreCase);
		return options with
		{
			PromptAnswers = promptAnswers,
			CustomGlobalNamedOptions = readonlyCustomGlobalValues,
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
		Dictionary<string, List<string>> customGlobalValues)
	{
		if (!TryResolveCustomGlobalName(argument, tokenMap, out var optionName, out var inlineValue))
		{
			return false;
		}

		var value = inlineValue;
		if (value is null
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

	private static bool IsSignedNumericLiteral(string token)
	{
		if (token.Length < 2 || token[0] != '-')
		{
			return false;
		}

		return double.TryParse(
			token,
			System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture,
			out _);
	}

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
