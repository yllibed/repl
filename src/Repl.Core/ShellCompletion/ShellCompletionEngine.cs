using Repl.Internal.Options;

namespace Repl;

/// <summary>
/// Provides shell (bash/zsh/fish/etc.) completion candidates for the Repl routing graph.
/// </summary>
internal sealed class ShellCompletionEngine(CoreReplApp app)
{

	public string[] ResolveShellCompletionCandidates(string line, int cursor)
	{
		// Completion must not poison the durable routing cache (see the interactive path).
		var activeGraph = app.ResolveActiveRoutingGraph(useDurableCache: false);
		var state = AnalyzeShellCompletionInput(line, cursor);
		if (state.PriorTokens.Length == 0)
		{
			return [];
		}

		var resolution = ResolveShellCommitted(state.PriorTokens, activeGraph);
		var commandPrefix = resolution.CommandPrefix;
		var optionsTerminated = resolution.OptionsTerminated;
		var routeMatch = resolution.Match;
		var currentTokenPrefix = state.CurrentTokenPrefix;
		// Same gate as the interactive menu: single-dash prefixes surface short option
		// aliases (-f); signed numeric literals stay positional. After the POSIX "--"
		// separator no option names may be offered — everything is positional.
		var currentTokenIsOption = !optionsTerminated && AutocompleteEngine.IsOptionPrefixToken(currentTokenPrefix);
		// Terminal-for-options only when every positional segment (required or optional) is
		// filled — an unfilled trailing segment would capture the accepted option/value.
		var hasTerminalRoute = routeMatch is not null
			&& commandPrefix.Length == routeMatch.Route.Template.Segments.Count;
		var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var candidates = new List<string>(capacity: 16);

		// The pending option is whatever the LAST committed token is (the first prior token is
		// the executable name). Enum-value completion only applies when that pending option is
		// the route's own trailing option — not a valued global that the resolution stripped
		// (e.g. "run --mode --tenant " is pending on --tenant, not --mode).
		var afterExecutable = state.PriorTokens.Length > 1 ? state.PriorTokens[1..] : [];
		var routeOptionIsLastCommitted = routeMatch is not null
			&& routeMatch.RemainingTokens.Count > 0
			&& afterExecutable.Length > 0
			&& string.Equals(afterExecutable[^1], routeMatch.RemainingTokens[^1], StringComparison.Ordinal);
		if (!currentTokenIsOption
			&& !optionsTerminated
			&& hasTerminalRoute
			&& routeOptionIsLastCommitted
			&& TryAddRouteEnumValueCandidates(
				routeMatch!,
				currentTokenPrefix,
				dedupe,
				candidates))
		{
			candidates.Sort(StringComparer.OrdinalIgnoreCase);
			return [.. candidates];
		}

		// A valued option still awaiting its value (and not an enum, which the block above
		// would have completed) makes the current token that value — offering a command or
		// option name here would misparse.
		if (app.Autocomplete.IsPendingOptionValue(afterExecutable, routeMatch, optionsTerminated, currentTokenPrefix))
		{
			return [];
		}

		AddShellCommandAndOptionCandidates(
			resolution, activeGraph, currentTokenPrefix, currentTokenIsOption, hasTerminalRoute, dedupe, candidates);
		candidates.Sort(StringComparer.OrdinalIgnoreCase);
		return [.. candidates];
	}

	private void AddShellCommandAndOptionCandidates(
		ShellResolution resolution,
		ActiveRoutingGraph activeGraph,
		string currentTokenPrefix,
		bool currentTokenIsOption,
		bool hasTerminalRoute,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		// No subcommand can follow once a terminal route already carries trailing option
		// tokens (see the interactive path).
		if (!currentTokenIsOption && resolution.Match is not { RemainingTokens.Count: > 0 })
		{
			AddShellCommandCandidates(
				resolution.CommandPrefix,
				currentTokenPrefix,
				activeGraph.Routes,
				activeGraph.Contexts,
				dedupe,
				candidates);
		}

		if (!resolution.OptionsTerminated
			&& (currentTokenIsOption || (string.IsNullOrEmpty(currentTokenPrefix) && hasTerminalRoute)))
		{
			AddShellOptionCandidates(
				hasTerminalRoute ? resolution.Match!.Route : null,
				currentTokenPrefix,
				candidates);
		}
	}

	// Mirrors the interactive engine's single resolution: the first prior token is the
	// executable name; global options are stripped with the arity-aware parser, unique
	// command prefixes are expanded (so "i" resolves to "install" like execution does), and
	// the route is resolved on the remaining tokens BEFORE option parsing — so dash tokens
	// and the bare "--" bind to segments as positional values. The match's trailing tokens
	// are the route's option region; a "--" among them terminates options, one bound to a
	// segment does not.
	private ShellResolution ResolveShellCommitted(string[] priorTokens, ActiveRoutingGraph activeGraph)
	{
		if (priorTokens.Length <= 1)
		{
			return new ShellResolution([], Match: null, OptionsTerminated: false);
		}

		var afterExecutable = new ArraySegment<string>(priorTokens, offset: 1, count: priorTokens.Length - 1);
		var stripped = GlobalOptionParser
			.Parse(afterExecutable, app.OptionsSnapshot.Output, app.OptionsSnapshot.Parsing)
			.RemainingTokens;
		var expanded = ExpandUniquePrefixes(stripped as string[] ?? [.. stripped], activeGraph);
		if (app.Resolve(expanded, activeGraph.Routes) is { } match)
		{
			var segmentCount = Math.Min(match.Route.Template.Segments.Count, expanded.Length);
			var optionsTerminated = false;
			foreach (var trailing in match.RemainingTokens)
			{
				if (string.Equals(trailing, "--", StringComparison.Ordinal))
				{
					optionsTerminated = true;
					break;
				}
			}

			return new ShellResolution(expanded[..segmentCount], match, optionsTerminated);
		}

		return new ShellResolution(expanded, Match: null, OptionsTerminated: false);
	}

	// Bounded unique-prefix/alias expansion mirroring the interactive engine.
	private string[] ExpandUniquePrefixes(string[] tokens, ActiveRoutingGraph activeGraph)
	{
		if (tokens.Length == 0)
		{
			return tokens;
		}

		var expansionDepth = 0;
		foreach (var route in activeGraph.Routes)
		{
			expansionDepth = Math.Max(expansionDepth, route.Template.Segments.Count);
		}

		foreach (var context in activeGraph.Contexts)
		{
			expansionDepth = Math.Max(expansionDepth, context.Template.Segments.Count);
		}

		if (tokens.Length <= expansionDepth)
		{
			var resolution = app.ResolveUniquePrefixes(tokens, activeGraph);
			return resolution.IsAmbiguous ? tokens : resolution.Tokens;
		}

		var headResolution = app.ResolveUniquePrefixes(tokens[..expansionDepth], activeGraph);
		return headResolution.IsAmbiguous
			? tokens
			: [.. headResolution.Tokens, .. tokens[expansionDepth..]];
	}

	private bool TryAddRouteEnumValueCandidates(
		RouteMatch match,
		string currentTokenPrefix,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		if (!TryResolvePendingRouteOption(match, out var entry))
		{
			return false;
		}

		if (!match.Route.OptionSchema.TryGetParameter(entry.ParameterName, out var parameter))
		{
			return false;
		}

		var enumType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
		if (!enumType.IsEnum)
		{
			return false;
		}

		var effectiveCaseSensitivity = parameter.CaseSensitivity ?? app.OptionsSnapshot.Parsing.OptionCaseSensitivity;
		var comparison = effectiveCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		var beforeCount = candidates.Count;
		foreach (var enumName in Enum
			         .GetNames(enumType)
			         .Where(name => name.StartsWith(currentTokenPrefix, comparison)))
		{
			TryAddShellCompletionCandidate(enumName, dedupe, candidates);
		}

		return candidates.Count > beforeCount;
	}

	private bool TryResolvePendingRouteOption(
		RouteMatch match,
		out OptionSchemaEntry entry)
	{
		entry = default!;

		// The pending option is the LAST token in the route's trailing option region — not a
		// dash-prefixed token that routing already bound to a positional segment. Deriving it
		// from match.RemainingTokens (rather than the raw prior tokens) is what keeps
		// "deploy -m" (where -m fills {target}) from being mistaken for a pending "-m" option.
		if (match.RemainingTokens.Count == 0)
		{
			return false;
		}

		var previousToken = match.RemainingTokens[^1];
		// A single dash is enough: short option aliases (e.g. "-m") take values too, and the
		// schema resolves them like any other token below.
		if (!AutocompleteEngine.IsOptionPrefixToken(previousToken))
		{
			return false;
		}

		var separatorIndex = previousToken.IndexOfAny(['=', ':']);
		if (separatorIndex >= 0)
		{
			return false;
		}

		var matches = match.Route.OptionSchema.ResolveToken(previousToken, app.OptionsSnapshot.Parsing.OptionCaseSensitivity);
		var distinct = matches
			.DistinctBy(candidate => (candidate.ParameterName, candidate.TokenKind, candidate.InjectedValue), ShellOptionSchemaEntryComparer.Instance)
			.ToArray();
		if (distinct.Length != 1)
		{
			return false;
		}

		if (distinct[0].TokenKind is not (OptionSchemaTokenKind.NamedOption or OptionSchemaTokenKind.BoolFlag))
		{
			return false;
		}

		entry = distinct[0];
		return true;
	}

	private static void TryAddShellCompletionCandidate(
		string candidate,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		if (string.IsNullOrWhiteSpace(candidate) || !dedupe.Add(candidate))
		{
			return;
		}

		candidates.Add(candidate);
	}

	private void AddShellCommandCandidates(
		string[] commandPrefix,
		string currentTokenPrefix,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		var matchingRoutes = app.Autocomplete.CollectVisibleMatchingRoutes(
			commandPrefix,
			StringComparison.OrdinalIgnoreCase,
			routes,
			contexts);
		foreach (var route in matchingRoutes)
		{
			if (commandPrefix.Length >= route.Template.Segments.Count
				|| route.Template.Segments[commandPrefix.Length] is not LiteralRouteSegment literal
				|| !literal.Value.StartsWith(currentTokenPrefix, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			TryAddShellCompletionCandidate(literal.Value, dedupe, candidates);
		}
	}

	private void AddShellOptionCandidates(
		RouteDefinition? route,
		string currentTokenPrefix,
		List<string> candidates)
	{
		// Option candidates dedupe with the PARSER's case semantics, not the command/UI
		// OrdinalIgnoreCase set: under case-sensitive option parsing, "-m" and "-M" can bind
		// to different parameters and both are executable, so they must not collapse. (Option
		// tokens start with '-' and never collide with command names, so a separate set is safe.)
		var optionDedupe = new HashSet<string>(
			app.OptionsSnapshot.Parsing.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
				? StringComparer.OrdinalIgnoreCase
				: StringComparer.Ordinal);
		AddGlobalShellOptionCandidates(currentTokenPrefix, optionDedupe, candidates);

		if (route is null)
		{
			return;
		}

		AddRouteShellOptionCandidates(route, currentTokenPrefix, optionDedupe, candidates);
	}

	private void AddGlobalShellOptionCandidates(
		string currentTokenPrefix,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		var options = app.OptionsSnapshot;
		OptionTokenCompletionSource.CollectGlobalOptionTokens(
			options,
			currentTokenPrefix,
			options.Parsing.OptionCaseSensitivity.ToStringComparison(),
			dedupe,
			candidates);
	}

	private void AddRouteShellOptionCandidates(
		RouteDefinition route,
		string currentTokenPrefix,
		HashSet<string> dedupe,
		List<string> candidates)
	{
		OptionTokenCompletionSource.CollectRouteOptionTokens(
			route,
			currentTokenPrefix,
			app.OptionsSnapshot.Parsing.OptionCaseSensitivity,
			dedupe,
			candidates);
	}

	internal static ShellCompletionInputState AnalyzeShellCompletionInput(string input, int cursor)
	{
		input ??= string.Empty;
		cursor = Math.Clamp(cursor, 0, input.Length);
		var tokens = AutocompleteEngine.TokenizeInputSpans(input);
		for (var i = 0; i < tokens.Count; i++)
		{
			var token = tokens[i];
			if (cursor < token.Start || cursor > token.End)
			{
				continue;
			}

			var prior = new string[i];
			for (var priorIndex = 0; priorIndex < i; priorIndex++)
			{
				prior[priorIndex] = tokens[priorIndex].Value;
			}

			var prefix = input[token.Start..cursor];
			return new ShellCompletionInputState(prior, prefix);
		}

		var trailingPriorCount = 0;
		foreach (var token in tokens)
		{
			if (token.End <= cursor)
			{
				trailingPriorCount++;
			}
		}

		if (trailingPriorCount == 0)
		{
			return new ShellCompletionInputState([], CurrentTokenPrefix: string.Empty);
		}

		var trailingPrior = new string[trailingPriorCount];
		var index = 0;
		foreach (var token in tokens)
		{
			if (token.End <= cursor)
			{
				trailingPrior[index++] = token.Value;
			}
		}

		return new ShellCompletionInputState(trailingPrior, CurrentTokenPrefix: string.Empty);
	}

	internal readonly record struct ShellCompletionInputState(
		string[] PriorTokens,
		string CurrentTokenPrefix);

	private readonly record struct ShellResolution(
		string[] CommandPrefix,
		RouteMatch? Match,
		bool OptionsTerminated);

	internal static string ResolveShellCompletionCommandName(
		IReadOnlyList<string>? commandLineArgs,
		string? processPath,
		string? fallbackName)
	{
		if (commandLineArgs is { Count: > 0 })
		{
			var commandHead = TryGetCommandHead(commandLineArgs[0]);
			if (!string.IsNullOrWhiteSpace(commandHead))
			{
				return commandHead;
			}
		}

		var processHead = TryGetCommandHead(processPath);
		if (!string.IsNullOrWhiteSpace(processHead))
		{
			return processHead;
		}

		return string.IsNullOrWhiteSpace(fallbackName) ? "repl" : fallbackName;
	}

	private static string? TryGetCommandHead(string? pathLike)
	{
		if (string.IsNullOrWhiteSpace(pathLike))
		{
			return null;
		}

		var fileName = Path.GetFileName(pathLike.Trim());
		if (string.IsNullOrWhiteSpace(fileName))
		{
			return null;
		}

		foreach (var extension in KnownExecutableExtensions)
		{
			if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
			{
				var head = fileName[..^extension.Length];
				return string.IsNullOrWhiteSpace(head) ? null : head;
			}
		}

		return fileName;
	}

	private static readonly string[] KnownExecutableExtensions =
	[
		".exe",
		".cmd",
		".bat",
		".com",
		".ps1",
		".dll",
	];

	public string ResolveShellCompletionCommandName()
	{
		var docApp = app.BuildDocumentationApp();
		return ResolveShellCompletionCommandName(
			Environment.GetCommandLineArgs(),
			Environment.ProcessPath,
			docApp.Name);
	}

	private sealed class ShellOptionSchemaEntryComparer : IEqualityComparer<(string ParameterName, OptionSchemaTokenKind TokenKind, string? InjectedValue)>
	{
		public static ShellOptionSchemaEntryComparer Instance { get; } = new();

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
