namespace Repl.Internal.Options;

/// <summary>
/// Single source of option-name completion candidates, shared by the interactive
/// autocomplete engine and the shell-completion engine so the two surfaces can never
/// drift: a global option or output format added here reaches both at once.
/// </summary>
internal static class OptionTokenCompletionSource
{
	/// <summary>
	/// Parser profile for normalizing completion-time tokens. Response files must NEVER be
	/// expanded on a completion path: interactive autocomplete runs per keystroke on lines
	/// that are remote-controlled in hosted sessions, so a '@file' token would mean
	/// server-side file reads (UNC probes included) driven by keystrokes — the execution
	/// pipeline gates expansion on !isInteractiveSession for exactly that reason. Shared by
	/// both completion engines; never mutated.
	/// </summary>
	internal static readonly ParsingOptions CompletionParsingOptions = new()
	{
		AllowUnknownOptions = true,
		AllowResponseFiles = false,
	};

	/// <summary>Framework-level options that exist regardless of app configuration.</summary>
	internal static readonly string[] StaticGlobalOptionTokens =
	[
		"--help",
		"--interactive",
		"--no-interactive",
		"--no-logo",
		"--output:",
		ReplResultFlowOptionNames.All,
		ReplResultFlowOptionNames.PageSize,
		ReplResultFlowOptionNames.Cursor,
		ReplResultFlowOptionNames.Pager,
	];

	/// <summary>
	/// Collects the global option tokens matching <paramref name="currentTokenPrefix"/>:
	/// static framework options, output-format aliases, <c>--output:&lt;format&gt;</c>
	/// selectors, and custom global options with their aliases.
	/// </summary>
	internal static void CollectGlobalOptionTokens(
		ReplOptions options,
		string currentTokenPrefix,
		StringComparison comparison,
		HashSet<string> dedupe,
		List<string> results)
	{
		foreach (var option in StaticGlobalOptionTokens)
		{
			TryAdd(option, currentTokenPrefix, comparison, dedupe, results);
		}

		// GlobalOptionParser.TryParsePromptAnswer matches "--answer:" with OrdinalIgnoreCase
		// regardless of the configured option case sensitivity, so it must be offered
		// case-insensitively too (a case-sensitive filter would hide it from "--ANS").
		TryAdd("--answer:", currentTokenPrefix, StringComparison.OrdinalIgnoreCase, dedupe, results);

		// Output-format aliases resolve through OutputOptions.Aliases, a case-insensitive
		// dictionary, so GlobalOptionParser accepts "--JSON" whatever the global option
		// case setting — completion must offer them on a differently-cased prefix too.
		foreach (var alias in options.Output.Aliases.Keys)
		{
			TryAddComposed("--", alias, currentTokenPrefix, StringComparison.OrdinalIgnoreCase, dedupe, results);
		}

		// Transformer names are also looked up through a case-insensitive dictionary, so
		// GlobalOptionParser accepts "--output:JSON" whatever the global option case setting.
		foreach (var format in options.Output.Transformers.Keys)
		{
			TryAddComposed("--output:", format, currentTokenPrefix, StringComparison.OrdinalIgnoreCase, dedupe, results);
		}

		foreach (var custom in options.Parsing.GlobalOptions.Values)
		{
			TryAdd(custom.CanonicalToken, currentTokenPrefix, comparison, dedupe, results);

			foreach (var alias in custom.Aliases)
			{
				TryAdd(alias, currentTokenPrefix, comparison, dedupe, results);
			}
		}
	}

	/// <summary>Collects the route's declared option tokens matching the prefix.</summary>
	internal static void CollectRouteOptionTokens(
		RouteDefinition route,
		string currentTokenPrefix,
		ReplCaseSensitivity globalCaseSensitivity,
		HashSet<string> dedupe,
		List<string> results) =>
		CollectRouteOptionTokens(route.OptionSchema, currentTokenPrefix, globalCaseSensitivity, dedupe, results);

	// Filters against the schema entries — not the flattened KnownTokens — so each option's
	// own case sensitivity is honored: an entry declared case-insensitive is offered for a
	// differently-cased prefix even under a case-sensitive global default (and vice versa),
	// matching exactly what the invocation parser accepts.
	internal static void CollectRouteOptionTokens(
		OptionSchema schema,
		string currentTokenPrefix,
		ReplCaseSensitivity globalCaseSensitivity,
		HashSet<string> dedupe,
		List<string> results)
	{
		foreach (var entry in schema.Entries)
		{
			var comparison = (entry.CaseSensitivity ?? globalCaseSensitivity) == ReplCaseSensitivity.CaseInsensitive
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;
			TryAdd(entry.Token, currentTokenPrefix, comparison, dedupe, results);
		}
	}

	private static void TryAdd(
		string token,
		string currentTokenPrefix,
		StringComparison comparison,
		HashSet<string> dedupe,
		List<string> results)
	{
		if (!token.StartsWith(currentTokenPrefix, comparison) || !dedupe.Add(token))
		{
			return;
		}

		results.Add(token);
	}

	// Composes "head + tail" candidates (e.g. "--" + alias) only when they can match:
	// the prefix test runs over the two parts so non-matching candidates never allocate
	// a throwaway string in this per-keystroke path.
	private static void TryAddComposed(
		string head,
		string tail,
		string currentTokenPrefix,
		StringComparison comparison,
		HashSet<string> dedupe,
		List<string> results)
	{
		if (!ComposedStartsWith(head, tail, currentTokenPrefix, comparison))
		{
			return;
		}

		var token = head + tail;
		if (dedupe.Add(token))
		{
			results.Add(token);
		}
	}

	private static bool ComposedStartsWith(string head, string tail, string prefix, StringComparison comparison)
	{
		if (prefix.Length <= head.Length)
		{
			return head.StartsWith(prefix, comparison);
		}

		return prefix.AsSpan(0, head.Length).Equals(head, comparison)
			&& tail.AsSpan().StartsWith(prefix.AsSpan(head.Length), comparison);
	}
}
