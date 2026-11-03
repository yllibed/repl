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
	/// <returns>The custom global token ownership used to filter lower-precedence route candidates.</returns>
	internal static IReadOnlyDictionary<string, GlobalOptionDefinition> CollectGlobalOptionTokens(
		ReplOptions options,
		ParsingOptions.GlobalOptionConfigurationSnapshot configuration,
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

		// Visibility is decided per TOKEN, not per definition: GlobalOptionParser gives a colliding
		// token to the LAST registration, so a token owned by a visible definition here may in fact
		// bind a hidden one. Build that authoritative ownership map once for this completion pass.
		var ownership = configuration.Ownership;
		foreach (var custom in configuration.Definitions.Values)
		{
			TryAddGlobalToken(custom.CanonicalToken, custom, configuration, currentTokenPrefix, comparison, dedupe, results);

			foreach (var alias in custom.Aliases)
			{
				TryAddGlobalToken(alias, custom, configuration, currentTokenPrefix, comparison, dedupe, results);
			}
		}

		return ownership;
	}

	private static void TryAddGlobalToken(
		string token,
		GlobalOptionDefinition expectedOwner,
		ParsingOptions.GlobalOptionConfigurationSnapshot configuration,
		string currentTokenPrefix,
		StringComparison comparison,
		HashSet<string> dedupe,
		List<string> results)
	{
		if (!GlobalOptionParser.IsGlobalTokenDiscoverable(token, expectedOwner, configuration))
		{
			return;
		}

		TryAdd(token, currentTokenPrefix, comparison, dedupe, results);
	}

	/// <summary>Collects the route's discoverable option tokens matching the prefix.</summary>
	// Filters against the schema entries — not the flattened KnownTokens — so each option's
	// own case sensitivity is honored: an entry declared case-insensitive is offered for a
	// differently-cased prefix even under a case-sensitive global default (and vice versa),
	// matching exactly what the invocation parser accepts. Hidden options are excluded by
	// DiscoverableEntries rather than by a caller-supplied predicate: a predicate parameter
	// was the very drift vector this single source exists to prevent.
	internal static void CollectRouteOptionTokens(
		OptionSchema schema,
		IReadOnlyDictionary<string, GlobalOptionDefinition> customGlobalOwnership,
		string currentTokenPrefix,
		ReplCaseSensitivity globalCaseSensitivity,
		HashSet<string> dedupe,
		List<string> results)
	{
		foreach (var entry in schema.ResolveDiscoverableEntries(globalCaseSensitivity))
		{
			// GlobalOptionParser consumes custom globals before route parsing. Suppress every
			// route token owned by that projection — including hidden globals that contributed no
			// candidate to dedupe — so completion never advertises a route binding execution cannot reach.
			if (customGlobalOwnership.ContainsKey(entry.Token))
			{
				continue;
			}

			var comparison = entry.EffectiveCaseSensitivity(globalCaseSensitivity) == ReplCaseSensitivity.CaseInsensitive
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
