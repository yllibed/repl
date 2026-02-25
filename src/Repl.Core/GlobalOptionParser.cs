namespace Repl;

internal static class GlobalOptionParser
{
	public static GlobalInvocationOptions Parse(IReadOnlyList<string> args, OutputOptions outputOptions)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(outputOptions);

		var remaining = new List<string>(args.Count);
		var promptAnswers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var options = new GlobalInvocationOptions(remaining);

		foreach (var argument in args)
		{
			switch (argument)
			{
				case "--help":
					options = options with { HelpRequested = true };
					continue;
				case "--interactive":
					options = options with { InteractiveForced = true };
					continue;
				case "--no-interactive":
					options = options with { InteractivePrevented = true };
					continue;
				case "--no-logo":
					options = options with { LogoSuppressed = true };
					continue;
			}

			if (argument.StartsWith("--output:", StringComparison.OrdinalIgnoreCase))
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

			remaining.Add(argument);
		}

		return options with { PromptAnswers = promptAnswers };
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
}
