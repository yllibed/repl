using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Repl.Internal.Options;

namespace Repl;

/// <summary>
/// Provides interactive autocomplete resolution for the Repl routing graph.
/// </summary>
internal sealed class AutocompleteEngine(CoreReplApp app)
{
	internal const string AutocompleteModeSessionStateKey = "__repl.autocomplete.mode";

	internal AutocompleteMode ResolveEffectiveAutocompleteMode(IServiceProvider serviceProvider)
	{
		var sessionState = serviceProvider.GetService(typeof(IReplSessionState)) as IReplSessionState;
		if (sessionState?.Get<string>(AutocompleteModeSessionStateKey) is { } overrideText
			&& Enum.TryParse<AutocompleteMode>(overrideText, ignoreCase: true, out var overrideMode))
		{
			return overrideMode == AutocompleteMode.Auto
				? ResolveAutoAutocompleteMode(serviceProvider)
				: overrideMode;
		}

		var configured = app.OptionsSnapshot.Interactive.Autocomplete.Mode;
		return configured == AutocompleteMode.Auto
			? ResolveAutoAutocompleteMode(serviceProvider)
			: configured;
	}

	private static AutocompleteMode ResolveAutoAutocompleteMode(IServiceProvider serviceProvider)
	{
		if (!ReplSessionIO.IsSessionActive
			&& !Console.IsInputRedirected
			&& !Console.IsOutputRedirected)
		{
			// Local interactive console: prefer rich rendering so menu redraw is in-place.
			return AutocompleteMode.Rich;
		}

		var info = serviceProvider.GetService(typeof(IReplSessionInfo)) as IReplSessionInfo;
		var caps = info?.TerminalCapabilities ?? TerminalCapabilities.None;
		if (caps.HasFlag(TerminalCapabilities.Ansi) && caps.HasFlag(TerminalCapabilities.VtInput))
		{
			return AutocompleteMode.Rich;
		}

		if (caps.HasFlag(TerminalCapabilities.VtInput) || caps.HasFlag(TerminalCapabilities.Ansi))
		{
			return AutocompleteMode.Basic;
		}

		return AutocompleteMode.Basic;
	}

	internal static ConsoleLineReader.AutocompleteRenderMode ResolveAutocompleteRenderMode(AutocompleteMode mode) =>
		mode switch
		{
			AutocompleteMode.Rich => ConsoleLineReader.AutocompleteRenderMode.Rich,
			AutocompleteMode.Basic => ConsoleLineReader.AutocompleteRenderMode.Basic,
			_ => ConsoleLineReader.AutocompleteRenderMode.Off,
		};

	internal ConsoleLineReader.AutocompleteColorStyles ResolveAutocompleteColorStyles(bool enabled)
	{
		if (!enabled)
		{
			return ConsoleLineReader.AutocompleteColorStyles.Empty;
		}

		var palette = app.OptionsSnapshot.Output.ResolvePalette();
		return new ConsoleLineReader.AutocompleteColorStyles(
			CommandStyle: palette.AutocompleteCommandStyle,
			ContextStyle: palette.AutocompleteContextStyle,
			ParameterStyle: palette.AutocompleteParameterStyle,
			AmbiguousStyle: palette.AutocompleteAmbiguousStyle,
			ErrorStyle: palette.AutocompleteErrorStyle,
			HintLabelStyle: palette.AutocompleteHintLabelStyle);
	}

	internal async ValueTask<ConsoleLineReader.AutocompleteResult?> ResolveAutocompleteAsync(
		ConsoleLineReader.AutocompleteRequest request,
		IReadOnlyList<string> scopeTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		// Completion must not poison the durable routing cache: its module-presence view can
		// differ from execution's (the line's globals are not applied here).
		var activeGraph = app.ResolveActiveRoutingGraph(useDurableCache: false);
		var comparer = app.OptionsSnapshot.Interactive.Autocomplete.CaseSensitive
			? StringComparer.Ordinal
			: StringComparer.OrdinalIgnoreCase;
		var prefixComparison = app.OptionsSnapshot.Interactive.Autocomplete.CaseSensitive
			? StringComparison.Ordinal
			: StringComparison.OrdinalIgnoreCase;
		var state = ResolveAutocompleteState(request, scopeTokens, prefixComparison, activeGraph);
		var matchingRoutes = CollectVisibleMatchingRoutes(
			state.CommandPrefix,
			prefixComparison,
			activeGraph.Routes,
			activeGraph.Contexts);
		var candidates = await CollectAutocompleteSuggestionsAsync(
				matchingRoutes,
				state.CommandPrefix,
				state.CurrentTokenPrefix,
				state.TerminalRoute,
				state.OptionsTerminated,
				state.PendingOptionValue,
				state.PendingOptionToken,
				request.ExplicitCompletion,
				scopeTokens.Count,
				activeGraph,
				prefixComparison,
				comparer,
				serviceProvider,
				cancellationToken)
			.ConfigureAwait(false);
		// Suppress the "Invalid" hint only for a pending value the parser would actually consume
		// as the option's value — the rule is option-kind-specific (see the helper).
		var suppressInvalidPendingHint = state.PendingOptionValue
			&& IsCurrentTokenConsumableAsPendingValue(state.PendingOptionToken, state.CurrentTokenPrefix);
		var liveHint = app.OptionsSnapshot.Interactive.Autocomplete.LiveHintEnabled
			? BuildLiveHint(
				matchingRoutes,
				candidates,
				state.CommandPrefix,
				state.CurrentTokenPrefix,
				suppressInvalidPendingHint,
				app.OptionsSnapshot.Interactive.Autocomplete.LiveHintMaxAlternatives)
			: null;
		var tokenClassifications = BuildTokenClassifications(
			request.Input,
			scopeTokens,
			prefixComparison,
			activeGraph,
			app.ResolveDiscoverableRoutes(activeGraph.Routes, activeGraph.Contexts, scopeTokens, prefixComparison),
			app.ResolveDiscoverableContexts(activeGraph.Contexts, scopeTokens, prefixComparison));
		return new ConsoleLineReader.AutocompleteResult(
			state.ReplaceStart,
			state.ReplaceLength,
			candidates,
			liveHint,
			tokenClassifications);
	}

	private AutocompleteResolutionState ResolveAutocompleteState(
		ConsoleLineReader.AutocompleteRequest request,
		IReadOnlyList<string> scopeTokens,
		StringComparison comparison,
		ActiveRoutingGraph activeGraph)
	{
		var state = AnalyzeAutocompleteInput(request.Input, request.Cursor);
		var committed = new string[scopeTokens.Count + state.PriorTokens.Length];
		for (var i = 0; i < scopeTokens.Count; i++)
		{
			committed[i] = scopeTokens[i];
		}

		state.PriorTokens.CopyTo(committed, scopeTokens.Count);

		var resolution = ResolveCommitted(committed, activeGraph);
		// When the last committed token is a valued option still awaiting its value, the
		// current token is that VALUE — not a command or another option. Suggesting either
		// would produce a different parse (e.g. "--tenant install" binds "install" as the
		// value), so the whole menu is suppressed for this position.
		if (IsPendingOptionValue(committed, resolution.TerminalRoute, resolution.OptionsTerminated, state.CurrentTokenPrefix))
		{
			return new AutocompleteResolutionState(
				resolution.CommandPrefix,
				state.CurrentTokenPrefix,
				state.ReplaceStart,
				state.ReplaceLength,
				resolution.TerminalRoute,
				resolution.OptionsTerminated)
			{
				PendingOptionValue = true,
				PendingOptionToken = committed[^1],
			};
		}

		var currentTokenPrefix = state.CurrentTokenPrefix;
		if (!ShouldAdvanceToNextToken(
				resolution.CommandPrefix,
				currentTokenPrefix,
				state.ReplaceStart,
				state.ReplaceLength,
				request.Cursor,
				comparison,
				activeGraph.Routes,
				activeGraph.Contexts))
		{
			return new AutocompleteResolutionState(
				resolution.CommandPrefix,
				currentTokenPrefix,
				state.ReplaceStart,
				state.ReplaceLength,
				resolution.TerminalRoute,
				resolution.OptionsTerminated);
		}

		// The current token is complete (whitespace follows): commit it and re-resolve so the
		// route, option boundary, and terminated state describe the advanced prefix.
		var advanced = ResolveCommitted([.. committed, currentTokenPrefix], activeGraph);
		return new AutocompleteResolutionState(
			advanced.CommandPrefix,
			string.Empty,
			request.Cursor,
			0,
			advanced.TerminalRoute,
			advanced.OptionsTerminated);
	}

	// Single source of truth for a completion pass: resolves the committed tokens the way
	// execution does (global options stripped by the arity-aware parser, then RouteResolver
	// on the remaining tokens — route resolution runs BEFORE option parsing, so dash tokens
	// and the bare "--" bind to segments as positionals). Everything downstream — the command
	// prefix, route-option sourcing, provider gating, token classification, and the
	// end-of-options state — is derived from this one result against the one captured graph,
	// so the surfaces cannot drift from each other or from the executor.
	private CommittedResolution ResolveCommitted(string[] committedTokens, ActiveRoutingGraph activeGraph)
	{
		if (committedTokens.Length == 0)
		{
			return new CommittedResolution([], TerminalRoute: null, OptionsTerminated: false);
		}

		var stripped = GlobalOptionParser
			.Parse(committedTokens, app.OptionsSnapshot.Output, app.OptionsSnapshot.Parsing)
			.RemainingTokens;
		var expanded = ExpandUniquePrefixes(stripped as string[] ?? [.. stripped], activeGraph);
		if (app.Resolve(expanded, activeGraph.Routes) is { } match)
		{
			var segmentCount = Math.Min(match.Route.Template.Segments.Count, expanded.Length);
			// The end-of-options separator only counts when it lands among the route's
			// trailing option tokens; a "--" consumed as a positional segment value does not
			// terminate options.
			var optionsTerminated = false;
			foreach (var trailing in match.RemainingTokens)
			{
				if (string.Equals(trailing, "--", StringComparison.Ordinal))
				{
					optionsTerminated = true;
					break;
				}
			}

			return new CommittedResolution(expanded[..segmentCount], match, optionsTerminated);
		}

		// No terminal route yet (still typing command words, or a positional is unfilled):
		// keep the tokens as typed so a dash-prefixed positional still satisfies its segment.
		return new CommittedResolution(expanded, TerminalRoute: null, OptionsTerminated: false);
	}

	private readonly record struct CommittedResolution(
		string[] CommandPrefix,
		RouteMatch? TerminalRoute,
		bool OptionsTerminated);

	// True when the last committed token is a valued option still awaiting its value, so the
	// current token is that value. Shared by the interactive engine and shell completion.
	internal bool IsPendingOptionValue(
		string[] committedTokens,
		RouteMatch? terminalRoute,
		bool optionsTerminated,
		string currentTokenPrefix)
	{
		if (committedTokens.Length == 0)
		{
			return false;
		}

		var last = committedTokens[^1];
		if (!IsOptionPrefixToken(last))
		{
			return false;
		}

		// A valued ROUTE option in the terminal route's trailing region is pending regardless
		// of the current token — the route parser will not consume a dash-prefixed token as
		// its value, so "run --channel --force" would leave --channel empty. But only BEFORE a
		// POSIX "--": after the separator the token is positional, not a route option.
		if (!optionsTerminated && IsPendingRouteOptionValue(last, terminalRoute))
		{
			return true;
		}

		// Global-scoped valued options (custom globals and built-in --result: suboptions) take
		// a SEPARATE value only when the next token is not option-like — the global parser skips
		// a dash-prefixed follower (so "--tenant --" is not pending; "--tenant acme" is).
		return !IsOptionPrefixToken(currentTokenPrefix) && IsPendingGlobalOptionValue(last);
	}

	private bool IsPendingRouteOptionValue(string last, RouteMatch? terminalRoute) =>
		!last.Contains('=', StringComparison.Ordinal)
		&& terminalRoute is { } match
		&& match.RemainingTokens.Count > 0
		&& string.Equals(match.RemainingTokens[^1], last, StringComparison.Ordinal)
		&& match.Route.OptionSchema
			.ResolveToken(last, app.OptionsSnapshot.Parsing.OptionCaseSensitivity)
			.Any(static entry => entry.TokenKind == OptionSchemaTokenKind.NamedOption);

	private bool IsPendingGlobalOptionValue(string last)
	{
		if (IsPendingResultFlowOption(last))
		{
			return true;
		}

		if (last.Contains('=', StringComparison.Ordinal) || last.Contains(':', StringComparison.Ordinal))
		{
			return false;
		}

		// Resolve through the parser's own token map so a colliding alias picks the SAME
		// (last-registered) definition the parser would — scanning definitions independently
		// could pick a shadowed one with a different arity.
		return GlobalOptionParser.TryResolveCustomGlobalDefinition(last, app.OptionsSnapshot.Parsing, out var definition)
			&& definition.ValueType != typeof(bool);
	}

	// The built-in --result: suboptions that take a separate value (page-size/cursor/pager);
	// --result:all is a flag. An inline value (--result:page-size=8) is already satisfied.
	// The token match honors the configured option case sensitivity: under CaseInsensitive,
	// GlobalOptionParser accepts "--RESULT:PAGE-SIZE" and consumes the next token as the value,
	// so completion must recognize the same casing or it would offer a command that execution
	// would swallow as the page-size value.
	// Whether the parser would consume the current token as the pending option's value. The rule
	// is option-kind-specific: result-flow suboptions (GlobalOptionParser.TryParseResultFlowOption)
	// reject ANY dash-prefixed token — even a signed numeric like "-1" — while route and custom
	// global options bind a signed numeric literal as a value (ShouldConsumeFollowingTokenAsValue).
	private bool IsCurrentTokenConsumableAsPendingValue(string? pendingOptionToken, string currentTokenPrefix)
	{
		if (string.IsNullOrEmpty(currentTokenPrefix))
		{
			return true;
		}

		if (pendingOptionToken is not null && IsPendingResultFlowOption(pendingOptionToken))
		{
			return !currentTokenPrefix.StartsWith('-');
		}

		return InvocationOptionParser.ShouldConsumeFollowingTokenAsValue(currentTokenPrefix);
	}

	private bool IsPendingResultFlowOption(string token)
	{
		var comparison = app.OptionsSnapshot.Parsing.OptionCaseSensitivity.ToStringComparison();
		return (string.Equals(token, ReplResultFlowOptionNames.PageSize, comparison)
				|| string.Equals(token, ReplResultFlowOptionNames.Cursor, comparison)
				|| string.Equals(token, ReplResultFlowOptionNames.Pager, comparison))
			&& !token.Contains('=', StringComparison.Ordinal);
	}

	// Unique-prefix/alias expansion, bounded by the deepest template: beyond that depth no
	// literal can match, and the per-index candidate derivation must not scale with the
	// token count of a remote-controlled line (hosted sessions autocomplete per keystroke).
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

	private async ValueTask<ConsoleLineReader.AutocompleteSuggestion[]> CollectAutocompleteSuggestionsAsync(
		IReadOnlyList<RouteDefinition> matchingRoutes,
		string[] commandPrefix,
		string currentTokenPrefix,
		RouteMatch? terminalRoute,
		bool optionsTerminated,
		bool pendingOptionValue,
		string? pendingOptionToken,
		bool providersAllowed,
		int scopeTokenCount,
		ActiveRoutingGraph activeGraph,
		StringComparison prefixComparison,
		StringComparer comparer,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		// A valued option awaiting its value: the current token is that value. Offer ONLY the
		// pending option's own value provider (WithCompletion / enum values); command names and
		// option names would misparse. The positional dynamic path below cannot be reused here —
		// it runs the route's SOLE registered provider without checking which parameter it
		// targets, so it would offer another parameter's values under this option.
		if (pendingOptionValue)
		{
			var pendingCandidates = await CollectPendingOptionValueCandidatesAsync(
					terminalRoute,
					pendingOptionToken,
					optionsTerminated,
					currentTokenPrefix,
					providersAllowed,
					serviceProvider,
					cancellationToken)
				.ConfigureAwait(false);
			// Dedupe option VALUES case-sensitively: a string option value is case-significant at
			// execution, so provider results that differ only by case ("Prod"/"prod") must both
			// survive — the UI's (possibly case-insensitive) comparer would collapse them.
			return DeduplicateSuggestions(pendingCandidates, StringComparer.Ordinal);
		}

		return await CollectStandardAutocompleteSuggestionsAsync(
				matchingRoutes,
				commandPrefix,
				currentTokenPrefix,
				terminalRoute,
				optionsTerminated,
				providersAllowed,
				scopeTokenCount,
				activeGraph,
				prefixComparison,
				comparer,
				serviceProvider,
				cancellationToken)
			.ConfigureAwait(false);
	}

	// The non-pending candidate assembly: command names, dynamic value providers, option names,
	// child contexts, and ambient commands, merged and deduplicated. Split from the entry point
	// so the pending-value short-circuit above stays legible.
	private async ValueTask<ConsoleLineReader.AutocompleteSuggestion[]> CollectStandardAutocompleteSuggestionsAsync(
		IReadOnlyList<RouteDefinition> matchingRoutes,
		string[] commandPrefix,
		string currentTokenPrefix,
		RouteMatch? terminalRoute,
		bool optionsTerminated,
		bool providersAllowed,
		int scopeTokenCount,
		ActiveRoutingGraph activeGraph,
		StringComparison prefixComparison,
		StringComparer comparer,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var dynamicCandidates = await CollectDynamicAutocompleteCandidatesAsync(
				matchingRoutes, commandPrefix, currentTokenPrefix, terminalRoute, providersAllowed,
				prefixComparison, activeGraph, serviceProvider, cancellationToken)
			.ConfigureAwait(false);

		// Once a terminal route has trailing option tokens, the command path is complete — no
		// subcommand can follow (a later literal can't match past an option-occupied position,
		// and a bool flag's zero-or-one arity would otherwise swallow the suggested word).
		var commandCandidates = terminalRoute is { RemainingTokens.Count: > 0 }
			? []
			: CollectRouteAutocompleteCandidates(
				matchingRoutes,
				commandPrefix,
				currentTokenPrefix,
				prefixComparison);
		var optionCandidates = CollectOptionAutocompleteCandidates(
			commandPrefix,
			currentTokenPrefix,
			terminalRoute,
			optionsTerminated);
		// Same option-region guard as command candidates: once the terminal route carries
		// trailing option tokens, execution treats the next word as the route's option text,
		// so a child context is unreachable there and must not be offered.
		var contextCandidates = app.OptionsSnapshot.Interactive.Autocomplete.ShowContextAlternatives
				&& terminalRoute is not { RemainingTokens.Count: > 0 }
			? CollectContextAutocompleteCandidates(commandPrefix, currentTokenPrefix, prefixComparison, activeGraph.Contexts)
			: [];
		// Ambient commands (help/exit/…) share the option-region guard: once the terminal route
		// carries trailing option tokens, "--force exit" is routed option text, not an ambient
		// invocation, so no ambient candidate may be offered at this position.
		var inOptionRegion = terminalRoute is { RemainingTokens.Count: > 0 };
		var ambientCandidates = commandPrefix.Length == scopeTokenCount && !inOptionRegion
			? CollectAmbientAutocompleteCandidates(currentTokenPrefix, prefixComparison)
			: [];
		var ambientContinuationCandidates = inOptionRegion
			? []
			: CollectAmbientContinuationAutocompleteCandidates(
				commandPrefix,
				currentTokenPrefix,
				scopeTokenCount,
				prefixComparison,
				activeGraph.Routes,
				activeGraph.Contexts);

		var candidates = DeduplicateSuggestions(
			commandCandidates
				.Concat(dynamicCandidates)
				.Concat(optionCandidates)
				.Concat(contextCandidates)
				.Concat(ambientCandidates)
				.Concat(ambientContinuationCandidates),
			comparer);
		return AppendInvalidAlternativeIfNeeded(candidates, currentTokenPrefix);
	}

	// When nothing selectable matched and invalid alternatives are shown, append the current
	// token as an explicit "(invalid)" entry so the user sees why the menu is empty.
	private ConsoleLineReader.AutocompleteSuggestion[] AppendInvalidAlternativeIfNeeded(
		ConsoleLineReader.AutocompleteSuggestion[] candidates,
		string currentTokenPrefix)
	{
		if (!app.OptionsSnapshot.Interactive.Autocomplete.ShowInvalidAlternatives
			|| string.IsNullOrWhiteSpace(currentTokenPrefix)
			|| candidates.Any(static candidate => candidate.IsSelectable))
		{
			return candidates;
		}

		return
		[
			.. candidates,
			new ConsoleLineReader.AutocompleteSuggestion(
				currentTokenPrefix,
				DisplayText: $"{currentTokenPrefix} (invalid)",
				Kind: ConsoleLineReader.AutocompleteSuggestionKind.Invalid,
				IsSelectable: false),
		];
	}

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Ambient autocomplete candidates are kept together for discoverability.")]
	private List<ConsoleLineReader.AutocompleteSuggestion> CollectAmbientAutocompleteCandidates(
		string currentTokenPrefix,
		StringComparison comparison)
	{
		var suggestions = new List<ConsoleLineReader.AutocompleteSuggestion>();
		AddAmbientSuggestion(
			suggestions,
			value: "help",
			description: "Show help for current path or a specific path.",
			currentTokenPrefix,
			comparison);
		AddAmbientSuggestion(
			suggestions,
			value: "?",
			description: "Alias for help.",
			currentTokenPrefix,
			comparison);
		AddAmbientSuggestion(
			suggestions,
			value: "..",
			description: "Go up one level in interactive mode.",
			currentTokenPrefix,
			comparison);
		if (app.OptionsSnapshot.AmbientCommands.ExitCommandEnabled)
		{
			AddAmbientSuggestion(
				suggestions,
				value: "exit",
				description: "Leave interactive mode.",
				currentTokenPrefix,
				comparison);
		}

		if (app.OptionsSnapshot.AmbientCommands.ShowHistoryInHelp)
		{
			AddAmbientSuggestion(
				suggestions,
				value: "history",
				description: "Show command history.",
				currentTokenPrefix,
				comparison);
		}

		if (app.OptionsSnapshot.AmbientCommands.ShowCompleteInHelp)
		{
			AddAmbientSuggestion(
				suggestions,
				value: "complete",
				description: "Query completion provider.",
				currentTokenPrefix,
				comparison);
		}

		foreach (var cmd in app.OptionsSnapshot.AmbientCommands.CustomCommands.Values)
		{
			AddAmbientSuggestion(
				suggestions,
				value: cmd.Name,
				description: cmd.Description ?? string.Empty,
				currentTokenPrefix,
				comparison);
		}

		return suggestions;
	}


	private List<ConsoleLineReader.AutocompleteSuggestion> CollectOptionAutocompleteCandidates(
		string[] commandPrefix,
		string currentTokenPrefix,
		RouteMatch? terminalRoute,
		bool optionsTerminated)
	{
		if (optionsTerminated || !IsOptionPrefixToken(currentTokenPrefix))
		{
			return [];
		}

		var comparison = ResolveOptionStringComparison();
		var comparer = StringComparer.FromComparison(comparison);
		var tokens = new List<string>();
		var dedupe = new HashSet<string>(comparer);
		OptionTokenCompletionSource.CollectGlobalOptionTokens(
			app.OptionsSnapshot, currentTokenPrefix, comparison, dedupe, tokens);

		// Source route options from the single route this prefix resolves to (already
		// computed for the whole pass), and only when EVERY positional segment — required or
		// optional — is present: an unfilled trailing segment would capture the accepted
		// option token as its value before option parsing runs, so the completed command
		// would not behave as the menu implies.
		if (terminalRoute is { } match
			&& commandPrefix.Length == match.Route.Template.Segments.Count)
		{
			OptionTokenCompletionSource.CollectRouteOptionTokens(
				match.Route,
				currentTokenPrefix,
				app.OptionsSnapshot.Parsing.OptionCaseSensitivity,
				dedupe,
				tokens);
		}

		tokens.Sort(comparer);
		var suggestions = new List<ConsoleLineReader.AutocompleteSuggestion>(tokens.Count);
		foreach (var token in tokens)
		{
			suggestions.Add(new ConsoleLineReader.AutocompleteSuggestion(
				token,
				Kind: ConsoleLineReader.AutocompleteSuggestionKind.Parameter));
		}

		return suggestions;
	}

	private StringComparison ResolveOptionStringComparison() =>
		app.OptionsSnapshot.Parsing.OptionCaseSensitivity.ToStringComparison();

	private List<ConsoleLineReader.AutocompleteSuggestion> CollectAmbientContinuationAutocompleteCandidates(
		string[] commandPrefix,
		string currentTokenPrefix,
		int scopeTokenCount,
		StringComparison comparison,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts)
	{
		if (commandPrefix.Length <= scopeTokenCount)
		{
			return [];
		}

		var ambientToken = commandPrefix[scopeTokenCount];
		if (!CoreReplApp.IsHelpToken(ambientToken))
		{
			return [];
		}

		var helpPathPrefix = commandPrefix.Skip(scopeTokenCount + 1).ToArray();
		var suggestions = CollectHelpPathAutocompleteCandidates(helpPathPrefix, currentTokenPrefix, comparison, routes, contexts);
		if (suggestions.Count > 0 || string.IsNullOrWhiteSpace(currentTokenPrefix))
		{
			return suggestions;
		}

		// `help <path>` accepts arbitrary path text; keep it neutral instead of invalid.
		suggestions.Add(new ConsoleLineReader.AutocompleteSuggestion(
			currentTokenPrefix,
			Kind: ConsoleLineReader.AutocompleteSuggestionKind.Parameter));
		return suggestions;
	}

	private List<ConsoleLineReader.AutocompleteSuggestion> CollectHelpPathAutocompleteCandidates(
		string[] helpPathPrefix,
		string currentTokenPrefix,
		StringComparison comparison,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts)
	{
		var suggestions = new List<ConsoleLineReader.AutocompleteSuggestion>();
		var segmentIndex = helpPathPrefix.Length;
		foreach (var context in contexts)
		{
			if (app.IsContextSuppressedForDiscovery(context, helpPathPrefix, comparison))
			{
				continue;
			}

			if (!MatchesTemplatePrefix(context.Template, helpPathPrefix, comparison, app.OptionsSnapshot.Parsing)
				|| segmentIndex >= context.Template.Segments.Count)
			{
				continue;
			}

			if (context.Template.Segments[segmentIndex] is LiteralRouteSegment literal
				&& literal.Value.StartsWith(currentTokenPrefix, comparison))
			{
				suggestions.Add(new ConsoleLineReader.AutocompleteSuggestion(
					literal.Value,
					Description: context.Description,
					Kind: ConsoleLineReader.AutocompleteSuggestionKind.Context));
			}
		}

		foreach (var route in routes)
		{
			if (route.Command.IsHidden
				|| app.IsRouteSuppressedForDiscovery(route.Template, contexts, helpPathPrefix, comparison)
				|| !MatchesTemplatePrefix(route.Template, helpPathPrefix, comparison, app.OptionsSnapshot.Parsing)
				|| segmentIndex >= route.Template.Segments.Count)
			{
				continue;
			}

			if (route.Template.Segments[segmentIndex] is LiteralRouteSegment literal
				&& literal.Value.StartsWith(currentTokenPrefix, comparison))
			{
				suggestions.Add(new ConsoleLineReader.AutocompleteSuggestion(
					literal.Value,
					Description: route.Command.Description,
					Kind: ConsoleLineReader.AutocompleteSuggestionKind.Command));
			}
		}

		return suggestions;
	}

	private static void AddAmbientSuggestion(
		List<ConsoleLineReader.AutocompleteSuggestion> suggestions,
		string value,
		string description,
		string currentTokenPrefix,
		StringComparison comparison)
	{
		if (!value.StartsWith(currentTokenPrefix, comparison))
		{
			return;
		}

		suggestions.Add(new ConsoleLineReader.AutocompleteSuggestion(
			value,
			Description: description,
			Kind: ConsoleLineReader.AutocompleteSuggestionKind.Command));
	}

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Token advancement logic keeps route/context suppression checks centralized.")]
	private bool ShouldAdvanceToNextToken(
		string[] commandPrefix,
		string currentTokenPrefix,
		int replaceStart,
		int replaceLength,
		int cursor,
		StringComparison comparison,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts)
	{
		if (string.IsNullOrEmpty(currentTokenPrefix) || cursor != replaceStart + replaceLength)
		{
			return false;
		}

		var segmentIndex = commandPrefix.Length;
		var hasLiteralMatch = false;
		var hasDynamicOrContextMatch = false;
		foreach (var route in routes)
		{
			if (route.Command.IsHidden
				|| app.IsRouteSuppressedForDiscovery(route.Template, contexts, commandPrefix, comparison)
				|| segmentIndex >= route.Template.Segments.Count)
			{
				continue;
			}

			if (!MatchesRoutePrefix(route, commandPrefix, comparison, app.OptionsSnapshot.Parsing))
			{
				continue;
			}

			if (route.Template.Segments[segmentIndex] is LiteralRouteSegment literal
				&& string.Equals(literal.Value, currentTokenPrefix, comparison))
			{
				hasLiteralMatch = true;
				continue;
			}

			if (route.Template.Segments[segmentIndex] is DynamicRouteSegment dynamic
				&& RouteConstraintEvaluator.IsMatch(dynamic, currentTokenPrefix, app.OptionsSnapshot.Parsing))
			{
				hasDynamicOrContextMatch = true;
			}
		}

		foreach (var context in contexts)
		{
			if (app.IsContextSuppressedForDiscovery(context, commandPrefix, comparison))
			{
				continue;
			}

			if (segmentIndex >= context.Template.Segments.Count
				|| !MatchesContextPrefix(context.Template, commandPrefix, comparison, app.OptionsSnapshot.Parsing))
			{
				continue;
			}

			var segment = context.Template.Segments[segmentIndex];
			if (segment is LiteralRouteSegment literal
				&& string.Equals(literal.Value, currentTokenPrefix, comparison))
			{
				hasDynamicOrContextMatch = true;
				continue;
			}

			if (segment is DynamicRouteSegment dynamic
				&& RouteConstraintEvaluator.IsMatch(dynamic, currentTokenPrefix, app.OptionsSnapshot.Parsing))
			{
				hasDynamicOrContextMatch = true;
			}
		}

		return hasLiteralMatch && !hasDynamicOrContextMatch;
	}

	private static List<ConsoleLineReader.AutocompleteSuggestion> CollectRouteAutocompleteCandidates(
		IReadOnlyList<RouteDefinition> matchingRoutes,
		string[] commandPrefix,
		string currentTokenPrefix,
		StringComparison prefixComparison)
	{
		var candidates = new List<ConsoleLineReader.AutocompleteSuggestion>();
		foreach (var route in matchingRoutes)
		{
			if (commandPrefix.Length < route.Template.Segments.Count
				&& route.Template.Segments[commandPrefix.Length] is LiteralRouteSegment literal
				&& literal.Value.StartsWith(currentTokenPrefix, prefixComparison))
			{
				candidates.Add(new ConsoleLineReader.AutocompleteSuggestion(
					literal.Value,
					Description: route.Command.Description,
					Kind: ConsoleLineReader.AutocompleteSuggestionKind.Command));
			}
		}

		return candidates;
	}

	private List<ConsoleLineReader.AutocompleteSuggestion> CollectContextAutocompleteCandidates(
		string[] commandPrefix,
		string currentTokenPrefix,
		StringComparison comparison,
		IReadOnlyList<ContextDefinition> contexts)
	{
		var suggestions = new List<ConsoleLineReader.AutocompleteSuggestion>();
		var segmentIndex = commandPrefix.Length;
		foreach (var context in contexts)
		{
			if (app.IsContextSuppressedForDiscovery(context, commandPrefix, comparison))
			{
				continue;
			}

			if (!MatchesContextPrefix(context.Template, commandPrefix, comparison, app.OptionsSnapshot.Parsing))
			{
				continue;
			}

			if (segmentIndex >= context.Template.Segments.Count)
			{
				continue;
			}

			var segment = context.Template.Segments[segmentIndex];
			if (segment is LiteralRouteSegment literal)
			{
				AddContextLiteralCandidate(suggestions, literal, currentTokenPrefix, comparison);
				continue;
			}

			AddContextDynamicCandidate(
				suggestions,
				(DynamicRouteSegment)segment,
				commandPrefix,
				currentTokenPrefix);
		}

		return suggestions
			.OrderBy(static suggestion => suggestion.DisplayText, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private static void AddContextLiteralCandidate(
		List<ConsoleLineReader.AutocompleteSuggestion> suggestions,
		LiteralRouteSegment literal,
		string currentTokenPrefix,
		StringComparison comparison)
	{
		if (!literal.Value.StartsWith(currentTokenPrefix, comparison))
		{
			return;
		}

		suggestions.Add(new ConsoleLineReader.AutocompleteSuggestion(
			literal.Value,
			DisplayText: literal.Value,
			Kind: ConsoleLineReader.AutocompleteSuggestionKind.Context));
	}

	private void AddContextDynamicCandidate(
		List<ConsoleLineReader.AutocompleteSuggestion> suggestions,
		DynamicRouteSegment dynamic,
		IReadOnlyList<string> commandPrefix,
		string currentTokenPrefix)
	{
		var placeholderValue = $"{{{dynamic.Name}}}";
		if (string.IsNullOrWhiteSpace(currentTokenPrefix))
		{
			suggestions.Add(new ConsoleLineReader.AutocompleteSuggestion(
				Value: string.Empty,
				DisplayText: placeholderValue,
				Description: $"Context [{BuildContextTargetPath(commandPrefix, placeholderValue)}]",
				Kind: ConsoleLineReader.AutocompleteSuggestionKind.Context,
				IsSelectable: false));
			return;
		}

		if (RouteConstraintEvaluator.IsMatch(dynamic, currentTokenPrefix, app.OptionsSnapshot.Parsing))
		{
			suggestions.Add(new ConsoleLineReader.AutocompleteSuggestion(
				currentTokenPrefix,
				DisplayText: placeholderValue,
				Description: $"Context [{BuildContextTargetPath(commandPrefix, currentTokenPrefix)}]",
				Kind: ConsoleLineReader.AutocompleteSuggestionKind.Context));
			return;
		}

		if (!app.OptionsSnapshot.Interactive.Autocomplete.ShowInvalidAlternatives)
		{
			return;
		}

		suggestions.Add(new ConsoleLineReader.AutocompleteSuggestion(
			currentTokenPrefix,
			DisplayText: $"{currentTokenPrefix} -> [invalid]",
			Kind: ConsoleLineReader.AutocompleteSuggestionKind.Invalid,
			IsSelectable: false));
	}

	private static string BuildContextTargetPath(IReadOnlyList<string> commandPrefix, string value)
	{
		var tokens = commandPrefix.Concat([value]).ToArray();
		return string.Join('/', tokens);
	}

	internal List<RouteDefinition> CollectVisibleMatchingRoutes(
		string[] commandPrefix,
		StringComparison prefixComparison,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts)
	{
		var matches = routes
			.Where(route =>
				!route.Command.IsHidden
				&& !app.IsRouteSuppressedForDiscovery(route.Template, contexts, commandPrefix, prefixComparison)
				&& MatchesRoutePrefix(route, commandPrefix, prefixComparison, app.OptionsSnapshot.Parsing))
			.ToList();
		if (commandPrefix.Length == 0)
		{
			return matches;
		}

		var literalMatches = matches
			.Where(route => MatchesLiteralPrefix(route.Template, commandPrefix, prefixComparison))
			.ToList();
		return literalMatches.Count > 0 ? literalMatches : matches;
	}

	private static bool MatchesLiteralPrefix(
		RouteTemplate template,
		string[] prefixTokens,
		StringComparison comparison)
	{
		if (prefixTokens.Length > template.Segments.Count)
		{
			return false;
		}

		for (var i = 0; i < prefixTokens.Length; i++)
		{
			if (template.Segments[i] is not LiteralRouteSegment literal
				|| !string.Equals(literal.Value, prefixTokens[i], comparison))
			{
				return false;
			}
		}

		return true;
	}

	private static string? BuildLiveHint(
		IReadOnlyList<RouteDefinition> matchingRoutes,
		IReadOnlyList<ConsoleLineReader.AutocompleteSuggestion> suggestions,
		string[] commandPrefix,
		string currentTokenPrefix,
		bool suppressInvalidPendingHint,
		int maxAlternatives)
	{
		var selectable = suggestions.Where(static suggestion => suggestion.IsSelectable).ToArray();
		var hintAlternatives = suggestions
			.Where(static suggestion =>
				suggestion.IsSelectable
				|| suggestion is
					{
						IsSelectable: false,
						Kind: ConsoleLineReader.AutocompleteSuggestionKind.Context,
					})
			.ToArray();
		if (selectable.Length == 0)
		{
			// A pending option value the parser would accept is not "Invalid" (the caller decides
			// consumability per option kind — result-flow rejects any dash-prefixed token, others
			// accept a signed numeric literal).
			if (suppressInvalidPendingHint)
			{
				return null;
			}

			return BuildDynamicHint(matchingRoutes, commandPrefix.Length, maxAlternatives)
				?? (string.IsNullOrWhiteSpace(currentTokenPrefix) ? null : $"Invalid: {currentTokenPrefix}");
		}

		// Option suggestions share AutocompleteSuggestionKind.Parameter, so without this
		// guard the parameter shortcut would hint the pending positional ("Param skillName")
		// while the menu is listing option names — the hint must describe what is shown.
		var segmentIndex = commandPrefix.Length;
		if (!IsOptionPrefixToken(currentTokenPrefix)
			&& TryBuildParameterHint(matchingRoutes, segmentIndex, out var parameterHint)
			&& selectable.All(static suggestion =>
				suggestion.Kind is ConsoleLineReader.AutocompleteSuggestionKind.Parameter
					or ConsoleLineReader.AutocompleteSuggestionKind.Invalid))
		{
			return parameterHint;
		}

		if (selectable.Length == 1)
		{
			return FormatSingleSelectionHint(selectable[0]);
		}

		maxAlternatives = Math.Max(1, maxAlternatives);
		var shown = hintAlternatives
			.Select(static suggestion => suggestion.DisplayText)
			.Take(maxAlternatives)
			.ToArray();
		var suffix = hintAlternatives.Length > shown.Length
			? $" (+{hintAlternatives.Length - shown.Length})"
			: string.Empty;
		return $"Matches: {string.Join(", ", shown)}{suffix}";
	}

	private static string FormatSingleSelectionHint(ConsoleLineReader.AutocompleteSuggestion suggestion)
	{
		if (suggestion.Kind == ConsoleLineReader.AutocompleteSuggestionKind.Command)
		{
			return string.IsNullOrWhiteSpace(suggestion.Description)
				? $"Command: {suggestion.DisplayText}"
				: $"Command: {suggestion.DisplayText} - {suggestion.Description}";
		}

		if (suggestion.Kind == ConsoleLineReader.AutocompleteSuggestionKind.Context)
		{
			return $"Context: {suggestion.DisplayText}";
		}

		return suggestion.DisplayText;
	}

	private static string? BuildDynamicHint(
		IReadOnlyList<RouteDefinition> matchingRoutes,
		int segmentIndex,
		int maxAlternatives)
	{
		if (TryBuildParameterHint(matchingRoutes, segmentIndex, out var parameterHint))
		{
			return parameterHint;
		}

		var dynamicRoutes = matchingRoutes
			.Where(route =>
				segmentIndex < route.Template.Segments.Count
				&& route.Template.Segments[segmentIndex] is DynamicRouteSegment)
			.Select(route => route.Template.Template)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (dynamicRoutes.Length <= 1)
		{
			return null;
		}

		maxAlternatives = Math.Max(1, maxAlternatives);
		var shown = dynamicRoutes.Take(maxAlternatives).ToArray();
		var suffix = dynamicRoutes.Length > shown.Length
			? $" (+{dynamicRoutes.Length - shown.Length})"
			: string.Empty;
		return $"Overloads: {string.Join(", ", shown)}{suffix}";
	}

	private static bool TryBuildParameterHint(
		IReadOnlyList<RouteDefinition> matchingRoutes,
		int segmentIndex,
		out string hint)
	{
		hint = string.Empty;
		var dynamicRoutes = matchingRoutes
			.Where(route =>
				segmentIndex < route.Template.Segments.Count
				&& route.Template.Segments[segmentIndex] is DynamicRouteSegment)
			.ToArray();
		if (dynamicRoutes.Length == 0)
		{
			return false;
		}

		if (dynamicRoutes.Length == 1
			&& dynamicRoutes[0].Template.Segments[segmentIndex] is DynamicRouteSegment singleDynamic)
		{
			var description = TryGetRouteParameterDescription(dynamicRoutes[0], singleDynamic.Name);
			hint = string.IsNullOrWhiteSpace(description)
				? $"Param {singleDynamic.Name}"
				: $"Param {singleDynamic.Name}: {description}";
			return true;
		}

		return false;
	}

	private static string? TryGetRouteParameterDescription(RouteDefinition route, string parameterName)
	{
		var parameter = route.Command.Handler.Method
			.GetParameters()
			.FirstOrDefault(parameter =>
				!string.IsNullOrWhiteSpace(parameter.Name)
				&& string.Equals(parameter.Name, parameterName, StringComparison.OrdinalIgnoreCase));
		return parameter?.GetCustomAttribute<DescriptionAttribute>()?.Description;
	}

	private async ValueTask<IReadOnlyList<ConsoleLineReader.AutocompleteSuggestion>> CollectDynamicAutocompleteCandidatesAsync(
		IReadOnlyList<RouteDefinition> matchingRoutes,
		string[] commandPrefix,
		string currentTokenPrefix,
		RouteMatch? terminalRoute,
		bool providersAllowed,
		StringComparison prefixComparison,
		ActiveRoutingGraph activeGraph,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var parsingOptions = app.OptionsSnapshot.Parsing;
		// Live-hint refreshes run after EVERY keystroke (MenuRequested: false): awaiting a
		// user provider there would freeze typing behind a slow lookup, so providers only run
		// for an explicit completion request (Tab/menu).
		if (!providersAllowed)
		{
			return [];
		}

		// Route resolution binds segments strictly positionally, so once the terminal route
		// carries trailing option tokens the positional slots of every longer route are
		// occupied by those tokens — no provider value typed here could ever bind at
		// execution (same region rule as command candidates). This also covers the POSIX
		// "--" separator: a "--" that did not bind to a segment IS a trailing token.
		if (terminalRoute is { RemainingTokens.Count: > 0 })
		{
			return [];
		}

		// A dash-prefixed token is NOT excluded here: route resolution binds a dash-prefixed
		// token to an unfilled positional ('deploy -prod' → target == "-prod"), so as long as
		// a positional target is open at this index the provider stays eligible — for the bare
		// '-', a partial '-pr', or a full '-prod'. Target resolution (only routes with a
		// dynamic segment here) and the per-candidate constraint check are the filters; option
		// NAMES keep their own separate menu, so the position's ambiguity surfaces both.
		var targets = ResolvePositionalCompletionTargets(
			matchingRoutes, commandPrefix, prefixComparison, parsingOptions);
		if (targets.Count == 0)
		{
			return [];
		}

		return await InvokePositionalProvidersAsync(
				commandPrefix,
				targets,
				DecodeTokenPrefix(currentTokenPrefix),
				activeGraph,
				serviceProvider,
				cancellationToken)
			.ConfigureAwait(false);
	}

	private async ValueTask<IReadOnlyList<ConsoleLineReader.AutocompleteSuggestion>> InvokePositionalProvidersAsync(
		string[] commandPrefix,
		IReadOnlyList<(RouteDefinition Route, DynamicRouteSegment Segment, CompletionDelegate Provider)> targets,
		string valuePrefix,
		ActiveRoutingGraph activeGraph,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var parsingOptions = app.OptionsSnapshot.Parsing;
		var numericFormatProvider = parsingOptions.NumericFormatProvider ?? CultureInfo.InvariantCulture;
		var completionContext = new CompletionContext(serviceProvider);
		var suggestions = new List<ConsoleLineReader.AutocompleteSuggestion>();
		// A value typed at the FIRST token position is dispatched as an ambient command (help,
		// exit, a custom ambient, ...) BEFORE routing, so it could never bind to a route value.
		// This is interactive-only: the CLI/shell dispatch path handles ambients differently, so
		// the shared CandidateBindsToProviderRoute deliberately does not encode it.
		var atFirstToken = commandPrefix.Length == 0;
		var ambientCommands = app.OptionsSnapshot.AmbientCommands;
		foreach (var target in targets)
		{
			var provided = await InvokeProviderSafelyAsync(
					target.Provider, completionContext, valuePrefix, cancellationToken)
				.ConfigureAwait(false);
			foreach (var item in provided)
			{
				// Parity per candidate: the segment constraint AND the handler parameter type
				// must both accept it (an unconstrained {count} whose handler takes int would
				// otherwise offer "abc"), and it must resolve to THIS route — a value that a
				// higher-scoring literal or an ambiguous prefix would claim never binds here.
				// Terminal controls are rejected before rendering; quotes added for the round-trip.
				if (!string.IsNullOrWhiteSpace(item)
					&& !(atFirstToken && InteractiveSession.IsAmbientFirstToken(item, ambientCommands))
					&& IsControlFreeValue(item)
					&& RouteConstraintEvaluator.IsMatch(target.Segment, item, parsingOptions)
					&& CandidateBindsToHandlerParameter(target.Route, target.Segment.Name, item, numericFormatProvider, parsingOptions.OptionCaseSensitivity)
					&& CandidateBindsToProviderRoute(commandPrefix, item, target.Route, activeGraph)
					&& QuoteValueForInsertion(item) is { } insertion)
				{
					suggestions.Add(new ConsoleLineReader.AutocompleteSuggestion(
						insertion,
						Kind: ConsoleLineReader.AutocompleteSuggestionKind.Parameter));
				}
			}
		}

		return suggestions;
	}

	// A provider value only belongs in the menu if, once accepted, execution would route it
	// to the SEGMENT it was offered for. Resolving the full command with the candidate catches
	// the case where a higher-scoring literal wins (e.g. 'pick {name}' provider offers
	// 'status' while a literal 'pick status' route exists): accepting it would run the literal,
	// the value never binds to {name}, and the literal is already offered as a command
	// candidate. A null match means the candidate does not yet complete any route (a
	// still-incomplete multi-segment route), which is fine — nothing else can claim it either.
	// A provider value only belongs in the menu if, once accepted, execution would route it
	// to the SEGMENT it was offered for. This resolves the candidate exactly as execution
	// does — ResolveCommitted strips global options first and matches against the FULL active
	// route graph (including hidden routes), not the discovery-filtered visible set — so a
	// value shadowed by a higher-scoring or hidden literal ('pick status'), or one the global
	// parser would strip ('--no-logo'), is caught. A null terminal match means the candidate
	// does not yet complete any route (a still-incomplete multi-segment route), which is
	// fine — nothing else can claim it either. Shared with the shell bridge via app.Autocomplete.
	// A route segment's constraint can be LOOSER than the handler parameter it feeds: an
	// unconstrained {count} whose handler takes int leaves the segment as String, so the
	// constraint check alone would offer "abc". Validate the candidate against the handler
	// parameter's type too (matched by segment name). Shared by both surfaces.
	internal bool CandidateBindsToHandlerParameter(
		RouteDefinition route,
		string segmentName,
		string candidate,
		IFormatProvider numericFormatProvider,
		ReplCaseSensitivity optionCaseSensitivity)
	{
		var parameter = Array.Find(
			route.Command.Handler.Method.GetParameters(),
			p => string.Equals(p.Name, segmentName, StringComparison.OrdinalIgnoreCase));
		if (parameter is null || BindsBeforeRouteValues(parameter))
		{
			return true;
		}

		// Mirror HandlerArgumentBinder.ResolveEnumIgnoreCase so a case-sensitive route does not
		// offer 'prod' for enum member 'Prod' (which execution would reject): a segment that is
		// ALSO an option honors that option's explicit case sensitivity; a pure positional segment
		// falls back to the parsing default. Passing enumIgnoreCase:true unconditionally broke
		// candidate-to-execution parity for enum values under case-sensitive parsing.
		var effectiveCaseSensitivity =
			(route.OptionSchema.TryGetParameter(segmentName, out var schemaParameter)
				? schemaParameter.CaseSensitivity
				: null)
			?? optionCaseSensitivity;
		var enumIgnoreCase = effectiveCaseSensitivity == ReplCaseSensitivity.CaseInsensitive;

		// A route segment binds its single value via ConvertSingle against the WHOLE parameter
		// type (see HandlerArgumentBinder.BindParameter's RouteValues branch), so a collection
		// target must NOT be element-unwrapped here — a single value can never bind to it, and
		// element unwrapping would wrongly offer a candidate that fails at execution.
		return ParameterValueConverter.CanConvert(
			candidate, parameter.ParameterType, numericFormatProvider, enumIgnoreCase, unwrapCollections: false);
	}

	// True when HandlerArgumentBinder.BindParameter would bind this parameter BEFORE consulting
	// context.RouteValues, so the route value is never used for it and its type must not gate the
	// candidate. Mirrors that method's pre-route precedence exactly: CancellationToken, an explicit
	// binding direction ([FromContext]/[FromServices]), a typed global-options parameter
	// (UseGlobalOptions<T>), and a [ReplOptionsGroup] parameter. Framework-injected services
	// (IServiceProvider, ICoreReplApp, ...) are deliberately NOT here — the binder consults route
	// values BEFORE them, so a segment homonym does bind (and fail) exactly as completion predicts.
	private bool BindsBeforeRouteValues(System.Reflection.ParameterInfo parameter) =>
		parameter.ParameterType == typeof(CancellationToken)
		|| parameter.GetCustomAttributes(typeof(FromContextAttribute), inherit: true).Length > 0
		|| parameter.GetCustomAttributes(typeof(FromServicesAttribute), inherit: true).Length > 0
		|| app.ImplicitServiceParameters.TryGetGlobalOptionsServiceType(parameter.ParameterType, out _)
		|| Attribute.IsDefined(parameter.ParameterType, typeof(ReplOptionsGroupAttribute), inherit: true);

	internal bool CandidateBindsToProviderRoute(
		string[] commandPrefix,
		string candidate,
		RouteDefinition providerRoute,
		ActiveRoutingGraph activeGraph)
	{
		var tokens = new string[commandPrefix.Length + 1];
		commandPrefix.CopyTo(tokens, 0);
		tokens[^1] = candidate;

		// Mirror execution's first step: strip global options. A candidate the global parser
		// consumes ('--no-logo', '--result:...') never reaches the segment, so it must not be
		// offered as a positional value — it would vanish rather than bind.
		var stripped = GlobalOptionParser
			.Parse(tokens, app.OptionsSnapshot.Output, app.OptionsSnapshot.Parsing)
			.RemainingTokens;
		var remaining = stripped as string[] ?? [.. stripped];
		if (remaining.Length <= commandPrefix.Length
			|| !string.Equals(remaining[commandPrefix.Length], candidate, StringComparison.Ordinal))
		{
			return false;
		}

		// Expand unique command prefixes exactly as execution does BEFORE routing: a value that
		// is a unique prefix of a literal sibling ('sta' for a literal 'status') expands to that
		// literal at execution, so it must be vetted expanded, not raw. An AMBIGUOUS prefix
		// ('st' matching both 'status' and 'staging') makes execution stop at the ambiguity
		// error before the segment is ever bound, so such a value can never bind — drop it.
		var prefixResolution = app.ResolveUniquePrefixes(remaining, activeGraph);
		if (prefixResolution.IsAmbiguous)
		{
			return false;
		}

		var expanded = prefixResolution.Tokens;

		// Unique-prefix expansion rewrites a token IN PLACE before routing: a provider value that
		// is itself a unique prefix of a literal sibling ('sta' for literal 'status') is expanded
		// to that literal, so execution binds 'status' — NOT the 'sta' the provider offered — even
		// when the winning route is still the provider's own (e.g. an INCOMPLETE 'pick status {id}'
		// sibling loses to the terminal 'pick {name}', yet the token was already rewritten).
		// Offering it would silently change the accepted value, so drop any candidate whose token
		// at the provider position was rewritten. (A candidate that expands to a literal owned by a
		// DIFFERENT winning route is already dropped by the route-identity check below.)
		if (expanded.Length <= commandPrefix.Length
			|| !string.Equals(expanded[commandPrefix.Length], candidate, StringComparison.Ordinal))
		{
			return false;
		}

		return ResolvesToProviderRoute(expanded, providerRoute, activeGraph);
	}

	// Resolve against the FULL active route graph (including hidden routes) the way execution
	// does, using the diagnostics so an INCOMPLETE route is judged too. A different winning
	// route — a higher-scoring or hidden literal ('pick status'), even while still missing later
	// arguments ('pick status {id}' outscoring 'pick {name} {id}') — shadows the value, so it
	// would never bind to the provider's segment (and a hidden command must not be surfaced
	// indirectly). Only when neither a terminal match nor a missing-argument winner claims the
	// tokens is the value genuinely unclaimed and safe to keep.
	private bool ResolvesToProviderRoute(
		string[] expanded,
		RouteDefinition providerRoute,
		ActiveRoutingGraph activeGraph)
	{
		var diagnostics = app.ResolveWithDiagnostics(expanded, activeGraph.Routes);
		if (diagnostics.Match is { } terminal)
		{
			return ReferenceEquals(terminal.Route, providerRoute);
		}

		// No terminal route match: dispatch next tries an EXACT context (deeplink). A candidate
		// whose expanded tokens exactly match a context navigates/renders that context instead of
		// binding to the provider's segment — even when the provider route is the missing-argument
		// winner ('pick {name} {id}' with a context 'pick status' shadows the value 'status') — so
		// it must not be offered.
		if (ContextResolver.ResolveExact(activeGraph.Contexts, expanded, app.OptionsSnapshot.Parsing) is not null)
		{
			return false;
		}

		if (diagnostics.MissingArgumentsFailure is { } incomplete)
		{
			return ReferenceEquals(incomplete.Route, providerRoute);
		}

		return true;
	}

	// Isolates an interactive completion provider's faults: a transient lookup provider
	// (database/network) that throws or returns a faulted task must drop only its own
	// suggestions, never abort the interactive session (the fault would otherwise escape
	// through ConsoleLineReader.ReadLineAsync). Real cancellation of the line read still
	// propagates so the reader unwinds cleanly.
	private static async ValueTask<IReadOnlyList<string>> InvokeProviderSafelyAsync(
		CompletionDelegate provider,
		CompletionContext completionContext,
		string input,
		CancellationToken cancellationToken)
	{
		try
		{
			return await provider(completionContext, input, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			return [];
		}
	}

	// The token being typed occupies the segment at the index right AFTER the committed
	// prefix, so providers are resolved by THAT dynamic segment's name. Gating on a fully
	// matched route instead would only fire once the value token is committed — one token
	// too late, on a position that can no longer bind to the parameter (issue #45) — and a
	// sole-registration lookup would run the wrong provider on multi-parameter commands.
	// Every matching route's provider participates: the PARTIAL typed token deliberately
	// plays no role here ('lookup 550e' is not yet a Guid, yet the provider's complete
	// candidates are exactly what the user needs) — suggestion/execution parity is enforced
	// per CANDIDATE against the returned segment's constraint instead. Shared with the shell
	// completion bridge so both surfaces resolve the same targets.
	internal static IReadOnlyList<(RouteDefinition Route, DynamicRouteSegment Segment, CompletionDelegate Provider)> ResolvePositionalCompletionTargets(
		IReadOnlyList<RouteDefinition> matchingRoutes,
		string[] commandPrefix,
		StringComparison prefixComparison,
		ParsingOptions parsingOptions)
	{
		var segmentIndex = commandPrefix.Length;
		List<(RouteDefinition, DynamicRouteSegment, CompletionDelegate)>? targets = null;
		foreach (var route in matchingRoutes)
		{
			if (segmentIndex < route.Template.Segments.Count
				&& route.Template.Segments[segmentIndex] is DynamicRouteSegment dynamicSegment
				&& MatchesRoutePrefix(route, commandPrefix, prefixComparison, parsingOptions)
				&& route.Command.Completions.TryGetValue(dynamicSegment.Name, out var completion))
			{
				(targets ??= []).Add((route, dynamicSegment, completion));
			}
		}

		return targets ?? (IReadOnlyList<(RouteDefinition, DynamicRouteSegment, CompletionDelegate)>)[];
	}

	// Decodes the raw lexical slice of the CURRENT token up to the cursor into its semantic
	// value prefix — the same quote state machine as TokenizeInputSpans, so '"Ne' decodes to
	// 'Ne' and a mid-token quote toggle is honored. Providers match against VALUES; feeding
	// them the raw quoted slice would miss ordinary prefix lookups. The quote-free fast path
	// returns the original string without allocating.
	internal static string DecodeTokenPrefix(string rawPrefix)
	{
		if (rawPrefix.AsSpan().IndexOfAny('"', '\'') < 0)
		{
			return rawPrefix;
		}

		var builder = new System.Text.StringBuilder(rawPrefix.Length);
		char? quote = null;
		foreach (var ch in rawPrefix)
		{
			if (quote is { } active && ch == active)
			{
				quote = null;
				continue;
			}

			if (quote is null && ch is '"' or '\'')
			{
				quote = ch;
				continue;
			}

			builder.Append(ch);
		}

		return builder.ToString();
	}

	// Provider values reflect external data (filenames, database labels): a value carrying
	// terminal control characters (C0 including ESC/BEL, DEL, C1 including the OSC/CSI
	// introducers) could retitle or corrupt the user's terminal when rendered in the menu or
	// inserted into the buffer, so it is rejected whole on every surface. Both range scans
	// are SIMD-accelerated (MemoryExtensions.ContainsAnyInRange).
	internal static bool IsControlFreeValue(string value) =>
		!value.AsSpan().ContainsAnyInRange('\u0000', '\u001F')
		&& !value.AsSpan().ContainsAnyInRange('\u007F', '\u009F');

	// A provider VALUE is semantic data while the suggestion list carries command-line
	// SYNTAX: a value containing whitespace or a quote is emitted pre-quoted so acceptance
	// round-trips through tokenization as ONE argument ('New York' -> "New York"). The
	// tokenizer has no escape sequences, so a value containing BOTH quote kinds cannot be
	// represented at all and yields null (the caller drops it).
	internal static string? QuoteValueForInsertion(string value)
	{
		var needsQuoting = false;
		var hasDoubleQuote = false;
		var hasSingleQuote = false;
		foreach (var ch in value)
		{
			if (char.IsWhiteSpace(ch))
			{
				needsQuoting = true;
			}
			else if (ch == '"')
			{
				hasDoubleQuote = true;
			}
			else if (ch == '\'')
			{
				hasSingleQuote = true;
			}
		}

		if (!needsQuoting && !hasDoubleQuote && !hasSingleQuote)
		{
			return value;
		}

		if (hasDoubleQuote && hasSingleQuote)
		{
			return null;
		}

		return hasDoubleQuote ? $"'{value}'" : $"\"{value}\"";
	}

	// Completes the VALUE of a pending route option by invoking ONLY that option's own provider.
	// It keys off the ACTUAL pending token (the last committed token), never the route match's
	// trailing token: ResolveCommitted strips globals before route resolution, so for
	// "run app --channel --tenant " the route's trailing token is still "--channel" while the
	// pending value belongs to the global "--tenant" — which carries no per-command provider,
	// so nothing is offered. Only a pending ROUTE option (same route-value condition the pending
	// detector uses) resolves a target parameter and runs its completion.
	private async ValueTask<IReadOnlyList<ConsoleLineReader.AutocompleteSuggestion>> CollectPendingOptionValueCandidatesAsync(
		RouteMatch? terminalRoute,
		string? pendingOptionToken,
		bool optionsTerminated,
		string currentTokenPrefix,
		bool providersAllowed,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (optionsTerminated
			|| pendingOptionToken is null
			|| terminalRoute is not { } match
			|| !IsPendingRouteOptionValue(pendingOptionToken, terminalRoute))
		{
			return [];
		}

		var entries = match.Route.OptionSchema.ResolveToken(
			pendingOptionToken, app.OptionsSnapshot.Parsing.OptionCaseSensitivity);
		foreach (var entry in entries)
		{
			// Same keystroke rule as the positional path: providers only run for an explicit
			// completion request; live-hint refreshes fall through to the static enum fallback.
			if (providersAllowed
				&& match.Route.Command.Completions.TryGetValue(entry.ParameterName, out var completion))
			{
				var providerSuggestions = await InvokePendingOptionProviderAsync(
						match, entry, completion, currentTokenPrefix, serviceProvider, cancellationToken)
					.ConfigureAwait(false);

				// A null result means the provider FAULTED: mirror the shell bridge (which treats a
				// fault/timeout as "no provider answer") and fall through to the static enum
				// fallback below, so a transient provider failure does not hide the always-valid
				// enum members. A non-null result — even empty — is the provider's final answer and
				// deliberately suppresses the enum fallback, matching the shell's precedence.
				if (providerSuggestions is not null)
				{
					return providerSuggestions;
				}
			}

			// No explicit provider: complete enum member names when the target is an enum,
			// matching shell completion (which already offers them for a pending option).
			if (TryCollectEnumValueSuggestions(
					match.Route.OptionSchema, entry.ParameterName, currentTokenPrefix, out var enumSuggestions))
			{
				return enumSuggestions;
			}
		}

		return [];
	}

	// Runs a pending route option's value provider and filters its candidates the way execution
	// binds them: consumable as the option's separate value (parser rule), convertible to the
	// option parameter's type under its effective enum case sensitivity, control-free, then
	// quoted for insertion (unrepresentable values dropped). Returns null when the provider
	// FAULTED, so the caller can fall through to the static enum fallback (shell-bridge parity);
	// a non-null (possibly empty) list is the provider's final answer.
	private async ValueTask<IReadOnlyList<ConsoleLineReader.AutocompleteSuggestion>?> InvokePendingOptionProviderAsync(
		RouteMatch match,
		OptionSchemaEntry entry,
		CompletionDelegate completion,
		string currentTokenPrefix,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var completionContext = new CompletionContext(serviceProvider);
		IReadOnlyList<string> provided;
		try
		{
			provided = await completion(completionContext, DecodeTokenPrefix(currentTokenPrefix), cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
			// Provider faulted — signal the caller to use the enum fallback instead of returning
			// an empty list that would suppress it (the shell bridge behaves the same way).
			return null;
		}

		var hasParameter = match.Route.OptionSchema.TryGetParameter(entry.ParameterName, out var optionParameter);
		var optionType = hasParameter ? optionParameter.ParameterType : typeof(string);
		var numericFormatProvider = app.OptionsSnapshot.Parsing.NumericFormatProvider ?? CultureInfo.InvariantCulture;
		var effectiveCaseSensitivity = (hasParameter ? optionParameter.CaseSensitivity : null)
			?? app.OptionsSnapshot.Parsing.OptionCaseSensitivity;
		var enumIgnoreCase = effectiveCaseSensitivity == ReplCaseSensitivity.CaseInsensitive;
		return provided
			.Where(item => !string.IsNullOrWhiteSpace(item)
				&& IsControlFreeValue(item)
				&& InvocationOptionParser.ShouldConsumeFollowingTokenAsValue(item)
				&& ParameterValueConverter.CanConvert(item, optionType, numericFormatProvider, enumIgnoreCase))
			.Select(static item => QuoteValueForInsertion(item))
			.OfType<string>()
			.Select(static insertion => new ConsoleLineReader.AutocompleteSuggestion(
				insertion,
				Kind: ConsoleLineReader.AutocompleteSuggestionKind.Parameter))
			.ToArray();
	}

	// Offers the member names of an enum-typed option parameter as value suggestions, filtered
	// by the current prefix under the parameter's effective case sensitivity. Mirrors the shell
	// engine's TryAddRouteEnumValueCandidates so both surfaces complete enum values identically.
	private bool TryCollectEnumValueSuggestions(
		OptionSchema schema,
		string parameterName,
		string currentTokenPrefix,
		out ConsoleLineReader.AutocompleteSuggestion[] suggestions)
	{
		suggestions = [];
		if (!schema.TryGetParameter(parameterName, out var parameter))
		{
			return false;
		}

		var enumType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
		if (!enumType.IsEnum)
		{
			return false;
		}

		var comparison = (parameter.CaseSensitivity ?? app.OptionsSnapshot.Parsing.OptionCaseSensitivity)
			.ToStringComparison();
		// Dedupe by the enum's EFFECTIVE case sensitivity, not the UI comparer: C# enums may have
		// case-distinct members (e.g. Prod/prod), but under case-insensitive parsing execution
		// maps both spellings to the first member — so only one candidate is offered (as shell does).
		var seen = new HashSet<string>(StringComparer.FromComparison(comparison));
		suggestions = Enum.GetNames(enumType)
			.Where(name => name.StartsWith(currentTokenPrefix, comparison) && seen.Add(name))
			.Select(static name => new ConsoleLineReader.AutocompleteSuggestion(
				name,
				Kind: ConsoleLineReader.AutocompleteSuggestionKind.Parameter))
			.ToArray();
		return suggestions.Length > 0;
	}

	private static ConsoleLineReader.AutocompleteSuggestion[] DeduplicateSuggestions(
		IEnumerable<ConsoleLineReader.AutocompleteSuggestion> suggestions,
		StringComparer comparer)
	{
		var seen = new HashSet<string>(comparer);
		// Option tokens and parameter VALUES dedupe case-sensitively: under case-sensitive
		// option parsing, distinct executable aliases like "-m" and "-M" both survive, and a
		// positional/option value is bound verbatim at execution, so provider results that
		// differ only by case ("Prod"/"prod") are distinct values — the UI-level comparer
		// (case-insensitive by default) must not collapse either group.
		var seenOrdinal = new HashSet<string>(StringComparer.Ordinal);
		var distinct = new List<ConsoleLineReader.AutocompleteSuggestion>();
		foreach (var suggestion in suggestions)
		{
			if (string.IsNullOrWhiteSpace(suggestion.DisplayText))
			{
				continue;
			}

			var dedupesOrdinally = suggestion.Kind == ConsoleLineReader.AutocompleteSuggestionKind.Parameter
				|| IsOptionPrefixToken(suggestion.DisplayText);
			if (dedupesOrdinally
				? !seenOrdinal.Add(suggestion.DisplayText)
				: !seen.Add(suggestion.DisplayText))
			{
				continue;
			}

			distinct.Add(suggestion);
		}

		return [.. distinct];
	}

	private List<ConsoleLineReader.TokenClassification> BuildTokenClassifications(
		string input,
		IReadOnlyList<string> scopeTokens,
		StringComparison comparison,
		ActiveRoutingGraph activeGraph,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return [];
		}

		var tokenSpans = TokenizeInputSpans(input);
		if (tokenSpans.Count == 0)
		{
			return [];
		}

		var rawValues = new string[tokenSpans.Count];
		for (var i = 0; i < tokenSpans.Count; i++)
		{
			rawValues[i] = tokenSpans[i].Value;
		}

		// Global-option tokens colour as options regardless of the route region (a leading
		// "--no-logo" is executable before the command).
		var isGlobal = MarkGlobalOptionTokens(rawValues);

		// Resolve the positional path ONCE (not per token): route resolution gives the segment
		// boundary that separates positionals from the trailing option region. Everything below
		// derives from this single result, so classification cannot drift from completion and
		// the per-keystroke cost stays flat instead of scaling with the token count.
		var positional = new List<string>(scopeTokens.Count + tokenSpans.Count);
		positional.AddRange(scopeTokens);
		for (var i = 0; i < tokenSpans.Count; i++)
		{
			if (!isGlobal[i])
			{
				positional.Add(rawValues[i]);
			}
		}

		var resolution = ResolveCommitted([.. positional], activeGraph);
		return EmitTokenClassifications(
			tokenSpans, rawValues, isGlobal, scopeTokens, comparison, routes, contexts, resolution);
	}

	private List<ConsoleLineReader.TokenClassification> EmitTokenClassifications(
		List<TokenSpan> tokenSpans,
		string[] rawValues,
		bool[] isGlobal,
		IReadOnlyList<string> scopeTokens,
		StringComparison comparison,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts,
		CommittedResolution resolution)
	{
		var segmentBoundary = resolution.CommandPrefix.Length;
		var output = new List<ConsoleLineReader.TokenClassification>(tokenSpans.Count);
		var runningPrefix = new List<string>(scopeTokens);
		var positionalIndex = scopeTokens.Count;
		// The end-of-options separator is per-position, not a whole-input flag: options BEFORE
		// a "--" remain options; only tokens after it leave the option region. (Using the
		// resolution's whole-input OptionsTerminated here would retroactively invalidate a
		// valid earlier "--force".)
		var separatorSeen = false;
		for (var i = 0; i < tokenSpans.Count; i++)
		{
			var start = tokenSpans[i].Start;
			var length = tokenSpans[i].End - start;
			if (isGlobal[i])
			{
				output.Add(new ConsoleLineReader.TokenClassification(
					start, length, ConsoleLineReader.AutocompleteSuggestionKind.Parameter));
				continue;
			}

			var value = rawValues[i];
			var isTrailing = resolution.TerminalRoute is not null && positionalIndex >= segmentBoundary;

			// Past the matched segments a dash token is a route option, not a positional.
			// (A dash token still within the segments falls through to ClassifyToken and is
			// classified as the positional it binds to.)
			var kind = isTrailing && !separatorSeen && IsOptionPrefixToken(value)
				? ConsoleLineReader.AutocompleteSuggestionKind.Parameter
				: ClassifyToken(
					[.. runningPrefix],
					value,
					comparison,
					routes,
					contexts,
					scopeTokenCount: scopeTokens.Count,
					isFirstInputToken: positionalIndex == scopeTokens.Count);
			output.Add(new ConsoleLineReader.TokenClassification(start, length, kind));

			// A bare "--" in the trailing option region is the separator; tokens after it are
			// positional again.
			if (isTrailing && string.Equals(value, "--", StringComparison.Ordinal))
			{
				separatorSeen = true;
			}

			runningPrefix.Add(value);
			positionalIndex++;
		}

		return output;
	}

	// Marks which raw tokens the global-option parser consumes (a global flag or its value),
	// so classification treats them as options and keeps them out of the positional command
	// path. Uses the parser's exact surviving INDICES — not string-value matching, which
	// cannot tell which occurrence of a duplicate value ("--tenant show show") was consumed.
	private bool[] MarkGlobalOptionTokens(string[] rawValues)
	{
		var survivingIndices = GlobalOptionParser
			.Parse(rawValues, app.OptionsSnapshot.Output, app.OptionsSnapshot.Parsing)
			.RemainingTokenIndices;
		var isGlobal = new bool[rawValues.Length];
		Array.Fill(isGlobal, value: true);
		foreach (var index in survivingIndices)
		{
			isGlobal[index] = false;
		}

		return isGlobal;
	}

	internal static bool IsGlobalOptionToken(string token) =>
		token.StartsWith("--", StringComparison.Ordinal);

	/// <summary>
	/// True when <paramref name="token"/> reads as the beginning of an option for
	/// SUGGESTION purposes — distinct from <see cref="IsGlobalOptionToken"/> (the parser's
	/// classifier for double-dash options): a single dash already announces an option
	/// (short aliases like <c>-f</c>), but a signed numeric literal (<c>-42</c>) is a
	/// positional argument, mirroring the invocation parser's rule.
	/// </summary>
	internal static bool IsOptionPrefixToken(string token) =>
		token.Length > 0
		&& token[0] == '-'
		&& !InvocationOptionParser.IsSignedNumericLiteral(token);

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Token classification intentionally keeps full precedence rules in one place.")]
	private ConsoleLineReader.AutocompleteSuggestionKind ClassifyToken(
		string[] prefixTokens,
		string token,
		StringComparison comparison,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts,
		int scopeTokenCount,
		bool isFirstInputToken)
	{
		if (isFirstInputToken && HasAmbientCommandPrefix(token, comparison))
		{
			return ConsoleLineReader.AutocompleteSuggestionKind.Command;
		}

		if (TryClassifyAmbientContinuation(prefixTokens, scopeTokenCount, out var ambientKind))
		{
			return ambientKind;
		}

		var routeLiteralMatch = false;
		var routeDynamicMatch = false;
		foreach (var route in routes)
		{
			if (route.Command.IsHidden
				|| app.IsRouteSuppressedForDiscovery(route.Template, contexts, prefixTokens, comparison)
				|| !TryClassifyTemplateSegment(
					route.Template,
					prefixTokens,
					token,
					comparison,
					app.OptionsSnapshot.Parsing,
					out var routeKind))
			{
				continue;
			}

			routeLiteralMatch |= routeKind == ConsoleLineReader.AutocompleteSuggestionKind.Command;
			routeDynamicMatch |= routeKind == ConsoleLineReader.AutocompleteSuggestionKind.Parameter;
		}

		var contextMatch = contexts.Any(context =>
			!app.IsContextSuppressedForDiscovery(context, prefixTokens, comparison)
			&&
			TryClassifyTemplateSegment(
				context.Template,
				prefixTokens,
				token,
				comparison,
				app.OptionsSnapshot.Parsing,
				out _));
		if ((routeLiteralMatch || routeDynamicMatch) && contextMatch)
		{
			return ConsoleLineReader.AutocompleteSuggestionKind.Ambiguous;
		}

		if (contextMatch)
		{
			return ConsoleLineReader.AutocompleteSuggestionKind.Context;
		}

		if (routeLiteralMatch)
		{
			return ConsoleLineReader.AutocompleteSuggestionKind.Command;
		}

		if (routeDynamicMatch)
		{
			return ConsoleLineReader.AutocompleteSuggestionKind.Parameter;
		}

		return ConsoleLineReader.AutocompleteSuggestionKind.Invalid;
	}

	private static bool TryClassifyAmbientContinuation(
		string[] prefixTokens,
		int scopeTokenCount,
		out ConsoleLineReader.AutocompleteSuggestionKind kind)
	{
		kind = ConsoleLineReader.AutocompleteSuggestionKind.Invalid;
		if (prefixTokens.Length <= scopeTokenCount)
		{
			return false;
		}

		var ambientToken = prefixTokens[scopeTokenCount];
		if (CoreReplApp.IsHelpToken(ambientToken)
			|| string.Equals(ambientToken, "history", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(ambientToken, "complete", StringComparison.OrdinalIgnoreCase))
		{
			kind = ConsoleLineReader.AutocompleteSuggestionKind.Parameter;
			return true;
		}

		if (!string.Equals(ambientToken, "autocomplete", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		kind = prefixTokens.Length == scopeTokenCount + 1
			? ConsoleLineReader.AutocompleteSuggestionKind.Command
			: ConsoleLineReader.AutocompleteSuggestionKind.Parameter;
		return true;
	}

	private bool HasAmbientCommandPrefix(string token, StringComparison comparison)
	{
		if (string.IsNullOrWhiteSpace(token))
		{
			return false;
		}

		if ("help".StartsWith(token, comparison) || "?".StartsWith(token, comparison))
		{
			return true;
		}

		if ("..".StartsWith(token, comparison))
		{
			return true;
		}

		if (app.OptionsSnapshot.AmbientCommands.ExitCommandEnabled && "exit".StartsWith(token, comparison))
		{
			return true;
		}

		if (app.OptionsSnapshot.AmbientCommands.ShowHistoryInHelp && "history".StartsWith(token, comparison))
		{
			return true;
		}

		if (app.OptionsSnapshot.AmbientCommands.ShowCompleteInHelp && "complete".StartsWith(token, comparison))
		{
			return true;
		}

		return app.OptionsSnapshot.AmbientCommands.CustomCommands.Keys
			.Any(name => name.StartsWith(token, comparison));
	}

	private static bool TryClassifyTemplateSegment(
		RouteTemplate template,
		string[] prefixTokens,
		string token,
		StringComparison comparison,
		ParsingOptions parsingOptions,
		out ConsoleLineReader.AutocompleteSuggestionKind kind)
	{
		kind = ConsoleLineReader.AutocompleteSuggestionKind.Invalid;
		if (!MatchesTemplatePrefix(template, prefixTokens, comparison, parsingOptions)
			|| prefixTokens.Length >= template.Segments.Count)
		{
			return false;
		}

		var segment = template.Segments[prefixTokens.Length];
		if (segment is LiteralRouteSegment literal && literal.Value.StartsWith(token, comparison))
		{
			kind = ConsoleLineReader.AutocompleteSuggestionKind.Command;
			return true;
		}

		if (segment is DynamicRouteSegment dynamic
			&& RouteConstraintEvaluator.IsMatch(dynamic, token, parsingOptions))
		{
			kind = ConsoleLineReader.AutocompleteSuggestionKind.Parameter;
			return true;
		}

		return false;
	}

	// Alias-aware variant for ROUTE prefixes: the router accepts command aliases on the
	// terminal literal segment (RouteResolver.IsLiteralMatch), so autocomplete must too —
	// otherwise an alias invocation loses its route's option suggestions.
	private static bool MatchesRoutePrefix(
		RouteDefinition route,
		string[] prefixTokens,
		StringComparison comparison,
		ParsingOptions parsingOptions)
	{
		var segments = route.Template.Segments;
		if (prefixTokens.Length > segments.Count)
		{
			return false;
		}

		for (var i = 0; i < prefixTokens.Length; i++)
		{
			var token = prefixTokens[i];
			switch (segments[i])
			{
				case LiteralRouteSegment literal
					when !IsRouteLiteralMatch(route, literal, token, i, comparison):
					return false;
				case DynamicRouteSegment dynamic
					when !RouteConstraintEvaluator.IsMatch(dynamic, token, parsingOptions):
					return false;
			}
		}

		return true;
	}

	// Delegates to the router's own literal/alias rule so the two can never drift.
	private static bool IsRouteLiteralMatch(
		RouteDefinition route,
		LiteralRouteSegment literal,
		string token,
		int segmentIndex,
		StringComparison comparison) =>
		RouteResolver.IsLiteralMatch(route, literal, token, segmentIndex, route.Template.Segments.Count, comparison);

	private static bool MatchesContextPrefix(
		RouteTemplate template,
		string[] prefixTokens,
		StringComparison comparison,
		ParsingOptions parsingOptions)
	{
		return MatchesTemplatePrefix(template, prefixTokens, comparison, parsingOptions);
	}

	private static bool MatchesTemplatePrefix(
		RouteTemplate template,
		string[] prefixTokens,
		StringComparison comparison,
		ParsingOptions parsingOptions)
	{
		if (prefixTokens.Length > template.Segments.Count)
		{
			return false;
		}

		for (var i = 0; i < prefixTokens.Length; i++)
		{
			var token = prefixTokens[i];
			var segment = template.Segments[i];
			switch (segment)
			{
				case LiteralRouteSegment literal
					when !string.Equals(literal.Value, token, comparison):
					return false;
				case DynamicRouteSegment dynamic
					when !RouteConstraintEvaluator.IsMatch(dynamic, token, parsingOptions):
					return false;
			}
		}

		return true;
	}

	internal static List<TokenSpan> TokenizeInputSpans(string input)
	{
		var tokens = new List<TokenSpan>();
		var index = 0;
		while (index < input.Length)
		{
			while (index < input.Length && char.IsWhiteSpace(input[index]))
			{
				index++;
			}

			if (index >= input.Length)
			{
				break;
			}

			var start = index;
			var value = new System.Text.StringBuilder();
			char? quote = null;

			if (input[index] is '"' or '\'')
			{
				quote = input[index];
				index++; // skip opening quote
			}

			while (index < input.Length)
			{
				if (quote is not null && input[index] == quote.Value)
				{
					index++; // skip closing quote
					break;
				}

				if (quote is null && char.IsWhiteSpace(input[index]))
				{
					break;
				}

				if (quote is null && input[index] is '"' or '\'')
				{
					quote = input[index];
					index++; // skip opening quote mid-token
					continue;
				}

				value.Append(input[index]);
				index++;
			}

			tokens.Add(new TokenSpan(value.ToString(), start, index));
		}

		return tokens;
	}

	private static AutocompleteInputState AnalyzeAutocompleteInput(string input, int cursor)
	{
		input ??= string.Empty;
		cursor = Math.Clamp(cursor, 0, input.Length);
		var tokens = TokenizeInputSpans(input);

		for (var i = 0; i < tokens.Count; i++)
		{
			var token = tokens[i];
			if (cursor < token.Start || cursor > token.End)
			{
				continue;
			}

			var prefix = input[token.Start..cursor];
			var prior = CollectPriorTokenValues(tokens, i);
			return new AutocompleteInputState(
				prior,
				prefix,
				token.Start,
				token.End - token.Start);
		}

		var trailingCount = 0;
		for (var i = 0; i < tokens.Count; i++)
		{
			if (tokens[i].End <= cursor)
			{
				trailingCount++;
			}
		}

		var trailingPrior = CollectPriorTokenValues(tokens, trailingCount);
		return new AutocompleteInputState(
			trailingPrior,
			CurrentTokenPrefix: string.Empty,
			ReplaceStart: cursor,
			ReplaceLength: 0);
	}

	// Collects the raw token VALUES before the cursor. Normalization into a command prefix
	// (and the end-of-options state) is deferred to ResolveCommitted, which resolves the
	// route rather than guessing option arities or pre-classifying dash tokens.
	private static string[] CollectPriorTokenValues(List<TokenSpan> tokens, int count)
	{
		if (count == 0)
		{
			return [];
		}

		var values = new string[count];
		for (var i = 0; i < count; i++)
		{
			values[i] = tokens[i].Value;
		}

		return values;
	}

	private readonly record struct AutocompleteInputState(
		string[] PriorTokens,
		string CurrentTokenPrefix,
		int ReplaceStart,
		int ReplaceLength);

	private readonly record struct AutocompleteResolutionState(
		string[] CommandPrefix,
		string CurrentTokenPrefix,
		int ReplaceStart,
		int ReplaceLength,
		RouteMatch? TerminalRoute,
		bool OptionsTerminated)
	{
		// True when the current token is the value of a valued option still awaiting one:
		// no command or option-name suggestions may be offered for this position.
		public bool PendingOptionValue { get; init; }

		// The actual option token awaiting a value (the last committed token) when
		// PendingOptionValue is set — a route option or a global. The pending-value provider
		// path keys off this, not the route match's trailing token, so a global pending after
		// an earlier route option does not run that route option's provider.
		public string? PendingOptionToken { get; init; }
	}

	internal readonly record struct TokenSpan(string Value, int Start, int End);
}
