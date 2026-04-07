using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

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
		var activeGraph = app.ResolveActiveRoutingGraph();
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
				scopeTokens.Count,
				activeGraph,
				prefixComparison,
				comparer,
				serviceProvider,
				cancellationToken)
			.ConfigureAwait(false);
		var liveHint = app.OptionsSnapshot.Interactive.Autocomplete.LiveHintEnabled
			? BuildLiveHint(
				matchingRoutes,
				candidates,
				state.CommandPrefix,
				state.CurrentTokenPrefix,
				app.OptionsSnapshot.Interactive.Autocomplete.LiveHintMaxAlternatives)
			: null;
		var discoverableRoutes = app.ResolveDiscoverableRoutes(
			activeGraph.Routes,
			activeGraph.Contexts,
			scopeTokens,
			prefixComparison);
		var discoverableContexts = app.ResolveDiscoverableContexts(
			activeGraph.Contexts,
			scopeTokens,
			prefixComparison);
		var tokenClassifications = BuildTokenClassifications(
			request.Input,
			scopeTokens,
			prefixComparison,
			discoverableRoutes,
			discoverableContexts);
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
		var commandPrefix = scopeTokens.Concat(state.PriorTokens).ToArray();
		var currentTokenPrefix = state.CurrentTokenPrefix;
		var replaceStart = state.ReplaceStart;
		var replaceLength = state.ReplaceLength;
		if (!ShouldAdvanceToNextToken(
				commandPrefix,
				currentTokenPrefix,
				replaceStart,
				replaceLength,
				request.Cursor,
				comparison,
				activeGraph.Routes,
				activeGraph.Contexts))
		{
			return new AutocompleteResolutionState(
				commandPrefix,
				currentTokenPrefix,
				replaceStart,
				replaceLength);
		}

		return new AutocompleteResolutionState(
			commandPrefix.Concat([currentTokenPrefix]).ToArray(),
			string.Empty,
			request.Cursor,
			0);
	}

	private async ValueTask<ConsoleLineReader.AutocompleteSuggestion[]> CollectAutocompleteSuggestionsAsync(
		IReadOnlyList<RouteDefinition> matchingRoutes,
		string[] commandPrefix,
		string currentTokenPrefix,
		int scopeTokenCount,
		ActiveRoutingGraph activeGraph,
		StringComparison prefixComparison,
		StringComparer comparer,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var commandCandidates = CollectRouteAutocompleteCandidates(
			matchingRoutes,
			commandPrefix,
			currentTokenPrefix,
			prefixComparison);
		var dynamicCandidates = await CollectDynamicAutocompleteCandidatesAsync(
				matchingRoutes,
				commandPrefix,
				currentTokenPrefix,
				prefixComparison,
				app.OptionsSnapshot.Parsing,
				serviceProvider,
				cancellationToken)
			.ConfigureAwait(false);
		var contextCandidates = app.OptionsSnapshot.Interactive.Autocomplete.ShowContextAlternatives
			? CollectContextAutocompleteCandidates(commandPrefix, currentTokenPrefix, prefixComparison, activeGraph.Contexts)
			: [];
		var ambientCandidates = commandPrefix.Length == scopeTokenCount
			? CollectAmbientAutocompleteCandidates(currentTokenPrefix, prefixComparison)
			: [];
		var ambientContinuationCandidates = CollectAmbientContinuationAutocompleteCandidates(
			commandPrefix,
			currentTokenPrefix,
			scopeTokenCount,
			prefixComparison,
			activeGraph.Routes,
			activeGraph.Contexts);

		var candidates = DeduplicateSuggestions(
			commandCandidates
				.Concat(dynamicCandidates)
				.Concat(contextCandidates)
				.Concat(ambientCandidates)
				.Concat(ambientContinuationCandidates),
			comparer);
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

			if (!MatchesRoutePrefix(route.Template, commandPrefix, comparison, app.OptionsSnapshot.Parsing))
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
				&& MatchesRoutePrefix(route.Template, commandPrefix, prefixComparison, app.OptionsSnapshot.Parsing))
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
		int maxAlternatives)
	{
		if (IsGlobalOptionToken(currentTokenPrefix))
		{
			return null;
		}

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
			return BuildDynamicHint(matchingRoutes, commandPrefix.Length, maxAlternatives)
				?? (string.IsNullOrWhiteSpace(currentTokenPrefix) ? null : $"Invalid: {currentTokenPrefix}");
		}

		var segmentIndex = commandPrefix.Length;
		if (TryBuildParameterHint(matchingRoutes, segmentIndex, out var parameterHint)
			&& selectable.All(static suggestion =>
				suggestion.Kind is ConsoleLineReader.AutocompleteSuggestionKind.Parameter
					or ConsoleLineReader.AutocompleteSuggestionKind.Invalid))
		{
			return parameterHint;
		}

		if (selectable.Length == 1)
		{
			var suggestion = selectable[0];
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

	private static async ValueTask<IReadOnlyList<ConsoleLineReader.AutocompleteSuggestion>> CollectDynamicAutocompleteCandidatesAsync(
		IReadOnlyList<RouteDefinition> matchingRoutes,
		string[] commandPrefix,
		string currentTokenPrefix,
		StringComparison prefixComparison,
		ParsingOptions parsingOptions,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var exactRoute = matchingRoutes.FirstOrDefault(route =>
			route.Template.Segments.Count == commandPrefix.Length
			&& MatchesTemplatePrefix(
				route.Template,
				commandPrefix,
				prefixComparison,
				parsingOptions));
		if (exactRoute is null || exactRoute.Command.Completions.Count != 1)
		{
			return [];
		}

		var completion = exactRoute.Command.Completions.Values.Single();
		var completionContext = new CompletionContext(serviceProvider);
		var provided = await completion(completionContext, currentTokenPrefix, cancellationToken)
			.ConfigureAwait(false);
		return provided
			.Where(static item => !string.IsNullOrWhiteSpace(item))
			.Select(static item => new ConsoleLineReader.AutocompleteSuggestion(
				item,
				Kind: ConsoleLineReader.AutocompleteSuggestionKind.Parameter))
			.ToArray();
	}

	private static ConsoleLineReader.AutocompleteSuggestion[] DeduplicateSuggestions(
		IEnumerable<ConsoleLineReader.AutocompleteSuggestion> suggestions,
		StringComparer comparer)
	{
		var seen = new HashSet<string>(comparer);
		var distinct = new List<ConsoleLineReader.AutocompleteSuggestion>();
		foreach (var suggestion in suggestions)
		{
			if (string.IsNullOrWhiteSpace(suggestion.DisplayText) || !seen.Add(suggestion.DisplayText))
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

		var output = new List<ConsoleLineReader.TokenClassification>(tokenSpans.Count);
		for (var i = 0; i < tokenSpans.Count; i++)
		{
			if (IsGlobalOptionToken(tokenSpans[i].Value))
			{
				output.Add(new ConsoleLineReader.TokenClassification(
					tokenSpans[i].Start,
					tokenSpans[i].End - tokenSpans[i].Start,
					ConsoleLineReader.AutocompleteSuggestionKind.Parameter));
				continue;
			}

			var prefix = scopeTokens.Concat(
				tokenSpans.Take(i)
					.Where(static token => !IsGlobalOptionToken(token.Value))
					.Select(static token => token.Value)).ToArray();
			var kind = ClassifyToken(
				prefix,
				tokenSpans[i].Value,
				comparison,
				routes,
				contexts,
				scopeTokenCount: scopeTokens.Count,
				isFirstInputToken: i == 0);
			output.Add(new ConsoleLineReader.TokenClassification(
				tokenSpans[i].Start,
				tokenSpans[i].End - tokenSpans[i].Start,
				kind));
		}

		return output;
	}

	internal static bool IsGlobalOptionToken(string token) =>
		token.StartsWith("--", StringComparison.Ordinal) && token.Length >= 2;

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

	private static bool MatchesRoutePrefix(
		RouteTemplate template,
		string[] prefixTokens,
		StringComparison comparison,
		ParsingOptions parsingOptions)
	{
		return MatchesTemplatePrefix(template, prefixTokens, comparison, parsingOptions);
	}

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
					quote = null;
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
			var prior = tokens.Take(i)
				.Where(static tokenSpan => !IsGlobalOptionToken(tokenSpan.Value))
				.Select(static tokenSpan => tokenSpan.Value).ToArray();
			return new AutocompleteInputState(
				prior,
				prefix,
				token.Start,
				token.End - token.Start);
		}

		var trailingPrior = tokens
			.Where(token => token.End <= cursor && !IsGlobalOptionToken(token.Value))
			.Select(static token => token.Value).ToArray();
		return new AutocompleteInputState(
			trailingPrior,
			CurrentTokenPrefix: string.Empty,
			ReplaceStart: cursor,
			ReplaceLength: 0);
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
		int ReplaceLength);

	internal readonly record struct TokenSpan(string Value, int Start, int End);
}
