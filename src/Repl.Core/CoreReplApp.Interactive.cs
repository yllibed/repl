using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace Repl;

public sealed partial class CoreReplApp
{
	private bool ShouldEnterInteractive(GlobalInvocationOptions globalOptions, bool allowAuto)
	{
		if (globalOptions.InteractivePrevented)
		{
			return false;
		}

		if (globalOptions.InteractiveForced)
		{
			return true;
		}

		return _options.Interactive.InteractivePolicy switch
		{
			InteractivePolicy.Force => true,
			InteractivePolicy.Prevent => false,
			_ => allowAuto,
		};
	}

	private async ValueTask<int> RunInteractiveSessionAsync(
		IReadOnlyList<string> initialScopeTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		using var runtimeStateScope = PushRuntimeState(serviceProvider, isInteractiveSession: true);
		using var cancelHandler = new CancelKeyHandler();
		var scopeTokens = initialScopeTokens.ToList();
		var historyProvider = serviceProvider.GetService(typeof(IHistoryProvider)) as IHistoryProvider;
		string? lastHistoryEntry = null;
		await TryHandleShellCompletionStartupAsync(serviceProvider, cancellationToken).ConfigureAwait(false);
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var readResult = await ReadInteractiveInputAsync(
					scopeTokens,
					historyProvider,
					serviceProvider,
					cancellationToken)
				.ConfigureAwait(false);
			if (readResult.Escaped)
			{
				await ReplSessionIO.Output.WriteLineAsync().ConfigureAwait(false);
				continue; // Esc at bare prompt → fresh line.
			}

			var line = readResult.Line;
			if (line is null)
			{
				return 0;
			}

			var inputTokens = TokenizeInteractiveInput(line);
			if (inputTokens.Count == 0)
			{
				continue;
			}

			lastHistoryEntry = await TryAppendHistoryAsync(
					historyProvider,
					lastHistoryEntry,
					line,
					cancellationToken)
				.ConfigureAwait(false);

			var outcome = await DispatchInteractiveCommandAsync(
					inputTokens, scopeTokens, serviceProvider, cancelHandler, cancellationToken)
				.ConfigureAwait(false);
			if (outcome == AmbientCommandOutcome.Exit)
			{
				return 0;
			}
		}
	}

	private async ValueTask<ConsoleLineReader.ReadResult> ReadInteractiveInputAsync(
		IReadOnlyList<string> scopeTokens,
		IHistoryProvider? historyProvider,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		await ReplSessionIO.Output.WriteAsync(BuildPrompt(scopeTokens)).ConfigureAwait(false);
		await ReplSessionIO.Output.WriteAsync(' ').ConfigureAwait(false);
		var effectiveMode = ResolveEffectiveAutocompleteMode(serviceProvider);
		var renderMode = ResolveAutocompleteRenderMode(effectiveMode);
		var colorStyles = ResolveAutocompleteColorStyles(renderMode == ConsoleLineReader.AutocompleteRenderMode.Rich);
		return await ConsoleLineReader.ReadLineAsync(
				historyProvider,
				effectiveMode == AutocompleteMode.Off
					? null
					: (request, ct) => ResolveAutocompleteAsync(request, scopeTokens, serviceProvider, ct),
				renderMode,
				_options.Interactive.Autocomplete.MaxVisibleSuggestions,
				_options.Interactive.Autocomplete.Presentation,
				_options.Interactive.Autocomplete.LiveHintEnabled
					&& renderMode == ConsoleLineReader.AutocompleteRenderMode.Rich,
				_options.Interactive.Autocomplete.ColorizeInputLine,
				_options.Interactive.Autocomplete.ColorizeHintAndMenu,
				colorStyles,
				cancellationToken)
			.ConfigureAwait(false);
	}

	private static async ValueTask<string?> TryAppendHistoryAsync(
		IHistoryProvider? historyProvider,
		string? previousEntry,
		string line,
		CancellationToken cancellationToken)
	{
		if (historyProvider is null || string.Equals(line, previousEntry, StringComparison.Ordinal))
		{
			return previousEntry;
		}

		// Persist raw input before dispatch so ambient commands are also traceable.
		// Skip consecutive duplicates, like standard shell behavior (ignoredups).
		await historyProvider.AddAsync(entry: line, cancellationToken).ConfigureAwait(false);
		return line;
	}

	private async ValueTask<AmbientCommandOutcome> DispatchInteractiveCommandAsync(
		List<string> inputTokens,
		List<string> scopeTokens,
		IServiceProvider serviceProvider,
		CancelKeyHandler cancelHandler,
		CancellationToken cancellationToken)
	{
		var ambientOutcome = await TryHandleAmbientCommandAsync(
				inputTokens,
				scopeTokens,
				serviceProvider,
				isInteractiveSession: true,
				cancellationToken)
			.ConfigureAwait(false);
		if (ambientOutcome is AmbientCommandOutcome.Exit
			or AmbientCommandOutcome.Handled
			or AmbientCommandOutcome.HandledError)
		{
			return ambientOutcome;
		}

		var invocationTokens = scopeTokens.Concat(inputTokens).ToArray();
		var globalOptions = GlobalOptionParser.Parse(invocationTokens, _options.Output);
		var prefixResolution = ResolveUniquePrefixes(globalOptions.RemainingTokens);
		if (prefixResolution.IsAmbiguous)
		{
			var ambiguous = CreateAmbiguousPrefixResult(prefixResolution);
			_ = await RenderOutputAsync(ambiguous, globalOptions.OutputFormat, cancellationToken, isInteractive: true)
				.ConfigureAwait(false);
			return AmbientCommandOutcome.Handled;
		}

		var resolvedOptions = globalOptions with { RemainingTokens = prefixResolution.Tokens };
		await ExecuteWithCancellationAsync(resolvedOptions, scopeTokens, serviceProvider, cancelHandler, cancellationToken)
			.ConfigureAwait(false);
		return AmbientCommandOutcome.Handled;
	}

	private async ValueTask ExecuteWithCancellationAsync(
		GlobalInvocationOptions resolvedOptions,
		List<string> scopeTokens,
		IServiceProvider serviceProvider,
		CancelKeyHandler cancelHandler,
		CancellationToken cancellationToken)
	{
		using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		SetCommandTokenOnChannel(serviceProvider, commandCts.Token);
		cancelHandler.SetCommandCts(commandCts);

		try
		{
			await ExecuteInteractiveInputAsync(resolvedOptions, scopeTokens, serviceProvider, commandCts.Token)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			await ReplSessionIO.Output.WriteLineAsync("Cancelled.").ConfigureAwait(false);
		}
		finally
		{
			cancelHandler.SetCommandCts(cts: null);
			SetCommandTokenOnChannel(serviceProvider, default);
		}
	}

	private static void SetCommandTokenOnChannel(IServiceProvider serviceProvider, CancellationToken ct)
	{
		if (serviceProvider.GetService(typeof(IReplInteractionChannel)) is ICommandTokenReceiver receiver)
		{
			receiver.SetCommandToken(ct);
		}
	}


	private async ValueTask ExecuteInteractiveInputAsync(
		GlobalInvocationOptions globalOptions,
		List<string> scopeTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		if (globalOptions.HelpRequested)
		{
			_ = await RenderHelpAsync(globalOptions, cancellationToken).ConfigureAwait(false);
			return;
		}

		var resolution = ResolveWithDiagnostics(globalOptions.RemainingTokens, activeGraph.Routes);
		var match = resolution.Match;
		if (match is not null)
		{
			await ExecuteMatchedCommandAsync(match, globalOptions, serviceProvider, scopeTokens, cancellationToken).ConfigureAwait(false);
			return;
		}

		var contextMatch = ContextResolver.ResolveExact(activeGraph.Contexts, globalOptions.RemainingTokens, _options.Parsing);
		if (contextMatch is not null)
		{
			var contextValidation = await ValidateContextAsync(contextMatch, serviceProvider, cancellationToken)
				.ConfigureAwait(false);
			if (!contextValidation.IsValid)
			{
				_ = await RenderOutputAsync(
						contextValidation.Failure,
						globalOptions.OutputFormat,
						cancellationToken,
						isInteractive: true)
					.ConfigureAwait(false);
				return;
			}

			scopeTokens.Clear();
			scopeTokens.AddRange(globalOptions.RemainingTokens);

			if (contextMatch.Context.Banner is { } contextBanner && ShouldRenderBanner(globalOptions.OutputFormat))
			{
				await InvokeBannerAsync(contextBanner, serviceProvider, cancellationToken).ConfigureAwait(false);
			}

			return;
		}

		var failure = CreateRouteResolutionFailureResult(
			tokens: globalOptions.RemainingTokens,
			resolution.ConstraintFailure,
			resolution.MissingArgumentsFailure);
		_ = await RenderOutputAsync(
				failure,
				globalOptions.OutputFormat,
				cancellationToken,
				isInteractive: true)
			.ConfigureAwait(false);
	}

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Ambient command routing keeps dispatch table explicit and easy to scan.")]
	private async ValueTask<AmbientCommandOutcome> TryHandleAmbientCommandAsync(
		List<string> inputTokens,
		List<string> scopeTokens,
		IServiceProvider serviceProvider,
		bool isInteractiveSession,
		CancellationToken cancellationToken)
	{
		if (inputTokens.Count == 0)
		{
			return AmbientCommandOutcome.NotHandled;
		}

		var token = inputTokens[0];
		if (IsHelpToken(token))
		{
			var helpPath = scopeTokens.Concat(inputTokens.Skip(1)).ToArray();
			var helpText = BuildHumanHelp(helpPath);
			await ReplSessionIO.Output.WriteLineAsync(helpText).ConfigureAwait(false);
			return AmbientCommandOutcome.Handled;
		}

		if (inputTokens.Count == 1 && string.Equals(token, "..", StringComparison.Ordinal))
		{
			return await HandleUpAmbientCommandAsync(scopeTokens, isInteractiveSession).ConfigureAwait(false);
		}

		if (inputTokens.Count == 1 && string.Equals(token, "exit", StringComparison.OrdinalIgnoreCase))
		{
			return await HandleExitAmbientCommandAsync().ConfigureAwait(false);
		}

		if (string.Equals(token, "complete", StringComparison.OrdinalIgnoreCase))
		{
			_ = await HandleCompletionAmbientCommandAsync(
					inputTokens.Skip(1).ToArray(),
					scopeTokens,
					serviceProvider,
					cancellationToken)
				.ConfigureAwait(false);
			return AmbientCommandOutcome.Handled;
		}

		if (string.Equals(token, "autocomplete", StringComparison.OrdinalIgnoreCase))
		{
			return await HandleAutocompleteAmbientCommandAsync(
					inputTokens.Skip(1).ToArray(),
					serviceProvider,
					isInteractiveSession)
				.ConfigureAwait(false);
		}

		if (string.Equals(token, "history", StringComparison.OrdinalIgnoreCase))
		{
			return await HandleHistoryAmbientCommandAsync(
					inputTokens.Skip(1).ToArray(),
					serviceProvider,
					isInteractiveSession,
					cancellationToken)
				.ConfigureAwait(false);
		}

		return AmbientCommandOutcome.NotHandled;
	}

	private static async ValueTask<AmbientCommandOutcome> HandleUpAmbientCommandAsync(
		List<string> scopeTokens,
		bool isInteractiveSession)
	{
		if (scopeTokens.Count > 0)
		{
			scopeTokens.RemoveAt(scopeTokens.Count - 1);
			return AmbientCommandOutcome.Handled;
		}

		if (!isInteractiveSession)
		{
			await ReplSessionIO.Output.WriteLineAsync("Error: '..' is available only in interactive mode.").ConfigureAwait(false);
			return AmbientCommandOutcome.HandledError;
		}

		return AmbientCommandOutcome.Handled;
	}

	private async ValueTask<AmbientCommandOutcome> HandleExitAmbientCommandAsync()
	{
		if (_options.AmbientCommands.ExitCommandEnabled)
		{
			return AmbientCommandOutcome.Exit;
		}

		await ReplSessionIO.Output.WriteLineAsync("Error: exit command is disabled.").ConfigureAwait(false);
		return AmbientCommandOutcome.HandledError;
	}

	private static async ValueTask<AmbientCommandOutcome> HandleHistoryAmbientCommandAsync(
		IReadOnlyList<string> commandTokens,
		IServiceProvider serviceProvider,
		bool isInteractiveSession,
		CancellationToken cancellationToken)
	{
		if (!isInteractiveSession)
		{
			await ReplSessionIO.Output.WriteLineAsync("Error: history is available only in interactive mode.").ConfigureAwait(false);
			return AmbientCommandOutcome.HandledError;
		}

		await HandleHistoryAmbientCommandCoreAsync(commandTokens, serviceProvider, cancellationToken).ConfigureAwait(false);
		return AmbientCommandOutcome.Handled;
	}

	private async ValueTask<AmbientCommandOutcome> HandleAutocompleteAmbientCommandAsync(
		string[] commandTokens,
		IServiceProvider serviceProvider,
		bool isInteractiveSession)
	{
		if (!isInteractiveSession)
		{
			await ReplSessionIO.Output.WriteLineAsync("Error: autocomplete is available only in interactive mode.")
				.ConfigureAwait(false);
			return AmbientCommandOutcome.HandledError;
		}

		var sessionState = serviceProvider.GetService(typeof(IReplSessionState)) as IReplSessionState;
		if (commandTokens.Length == 0
			|| (commandTokens.Length == 1 && string.Equals(commandTokens[0], "show", StringComparison.OrdinalIgnoreCase)))
		{
			var configured = _options.Interactive.Autocomplete.Mode;
			var overrideMode = sessionState?.Get<string>(AutocompleteModeSessionStateKey);
			var effective = ResolveEffectiveAutocompleteMode(serviceProvider);
			await ReplSessionIO.Output.WriteLineAsync(
					$"Autocomplete mode: configured={configured}, override={(overrideMode ?? "none")}, effective={effective}")
				.ConfigureAwait(false);
			return AmbientCommandOutcome.Handled;
		}

		if (commandTokens.Length == 2 && string.Equals(commandTokens[0], "mode", StringComparison.OrdinalIgnoreCase))
		{
			if (!Enum.TryParse<AutocompleteMode>(commandTokens[1], ignoreCase: true, out var mode))
			{
				await ReplSessionIO.Output.WriteLineAsync("Error: autocomplete mode must be one of off|auto|basic|rich.")
					.ConfigureAwait(false);
				return AmbientCommandOutcome.HandledError;
			}

			sessionState?.Set(AutocompleteModeSessionStateKey, mode.ToString());
			var effective = ResolveEffectiveAutocompleteMode(serviceProvider);
			await ReplSessionIO.Output.WriteLineAsync($"Autocomplete mode set to {mode} (effective: {effective}).")
				.ConfigureAwait(false);
			return AmbientCommandOutcome.Handled;
		}

		await ReplSessionIO.Output.WriteLineAsync("Error: usage: autocomplete [show] | autocomplete mode <off|auto|basic|rich>.")
			.ConfigureAwait(false);
		return AmbientCommandOutcome.HandledError;
	}

	private async ValueTask<bool> HandleCompletionAmbientCommandAsync(
		IReadOnlyList<string> commandTokens,
		IReadOnlyList<string> scopeTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var parsed = InvocationOptionParser.Parse(commandTokens);
		if (!parsed.NamedOptions.TryGetValue("target", out var targetValues) || targetValues.Count == 0)
		{
			await ReplSessionIO.Output.WriteLineAsync("Error: complete requires --target <name>.").ConfigureAwait(false);
			return false;
		}

		var target = targetValues[0];
		var input = parsed.NamedOptions.TryGetValue("input", out var inputValues) && inputValues.Count > 0
			? inputValues[0]
			: string.Empty;
		var fullCommandPath = scopeTokens.Concat(parsed.PositionalArguments).ToArray();
		var resolvedPath = ResolveUniquePrefixes(fullCommandPath);
		if (resolvedPath.IsAmbiguous)
		{
			var ambiguous = CreateAmbiguousPrefixResult(resolvedPath);
			_ = await RenderOutputAsync(ambiguous, requestedFormat: null, cancellationToken).ConfigureAwait(false);
			return false;
		}

		var match = Resolve(resolvedPath.Tokens);
		if (match is null || match.RemainingTokens.Count > 0)
		{
			await ReplSessionIO.Output.WriteLineAsync("Error: complete requires a terminal command path.").ConfigureAwait(false);
			return false;
		}

		if (!match.Route.Command.Completions.TryGetValue(target, out var completion))
		{
			await ReplSessionIO.Output.WriteLineAsync($"Error: no completion provider registered for '{target}'.").ConfigureAwait(false);
			return false;
		}

		var context = new CompletionContext(serviceProvider);
		var candidates = await completion(context, input, cancellationToken).ConfigureAwait(false);
		if (candidates.Count == 0)
		{
			await ReplSessionIO.Output.WriteLineAsync("(none)").ConfigureAwait(false);
			return true;
		}

		foreach (var candidate in candidates)
		{
			await ReplSessionIO.Output.WriteLineAsync(candidate).ConfigureAwait(false);
		}

		return true;
	}

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "History command parsing and rendering are intentionally kept together.")]
	private static async ValueTask HandleHistoryAmbientCommandCoreAsync(
		IReadOnlyList<string> commandTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var parsed = InvocationOptionParser.Parse(commandTokens);
		var limit = 20;
		if (parsed.NamedOptions.TryGetValue("limit", out var limitValues) && limitValues.Count > 0)
		{
			limit = int.TryParse(
					limitValues[0],
					style: NumberStyles.Integer,
					provider: CultureInfo.InvariantCulture,
					result: out var parsedLimit) && parsedLimit > 0
				? parsedLimit
				: throw new InvalidOperationException("history --limit must be a positive integer.");
		}

		var historyProvider = serviceProvider.GetService(typeof(IHistoryProvider)) as IHistoryProvider;
		if (historyProvider is null)
		{
			await ReplSessionIO.Output.WriteLineAsync("(history unavailable)").ConfigureAwait(false);
			return;
		}

		var entries = await historyProvider.GetRecentAsync(maxCount: limit, cancellationToken).ConfigureAwait(false);
		if (entries.Count == 0)
		{
			await ReplSessionIO.Output.WriteLineAsync("(empty)").ConfigureAwait(false);
			return;
		}

		foreach (var entry in entries)
		{
			await ReplSessionIO.Output.WriteLineAsync(entry).ConfigureAwait(false);
		}
	}

	private string[] GetDeepestContextScopePath(IReadOnlyList<string> matchedPathTokens)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		var contextMatches = ContextResolver.ResolvePrefixes(activeGraph.Contexts, matchedPathTokens, _options.Parsing);
		var longestPrefixLength = 0;
		foreach (var contextMatch in contextMatches)
		{
			var prefixLength = contextMatch.Context.Template.Segments.Count;
			if (prefixLength > longestPrefixLength)
			{
				longestPrefixLength = prefixLength;
			}
		}

		return longestPrefixLength == 0
			? []
			: matchedPathTokens.Take(longestPrefixLength).ToArray();
	}

	private string BuildPrompt(IReadOnlyList<string> scopeTokens)
	{
		var basePrompt = _options.Interactive.Prompt;
		if (scopeTokens.Count == 0)
		{
			return basePrompt;
		}

		var promptWithoutSuffix = basePrompt.EndsWith('>')
			? basePrompt[..^1]
			: basePrompt;
		var scope = string.Join('/', scopeTokens);
		return string.IsNullOrWhiteSpace(promptWithoutSuffix)
			? $"[{scope}]>"
			: $"{promptWithoutSuffix} [{scope}]>";
	}

	private AutocompleteMode ResolveEffectiveAutocompleteMode(IServiceProvider serviceProvider)
	{
		var sessionState = serviceProvider.GetService(typeof(IReplSessionState)) as IReplSessionState;
		if (sessionState?.Get<string>(AutocompleteModeSessionStateKey) is { } overrideText
			&& Enum.TryParse<AutocompleteMode>(overrideText, ignoreCase: true, out var overrideMode))
		{
			return overrideMode == AutocompleteMode.Auto
				? ResolveAutoAutocompleteMode(serviceProvider)
				: overrideMode;
		}

		var configured = _options.Interactive.Autocomplete.Mode;
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

	private static ConsoleLineReader.AutocompleteRenderMode ResolveAutocompleteRenderMode(AutocompleteMode mode) =>
		mode switch
		{
			AutocompleteMode.Rich => ConsoleLineReader.AutocompleteRenderMode.Rich,
			AutocompleteMode.Basic => ConsoleLineReader.AutocompleteRenderMode.Basic,
			_ => ConsoleLineReader.AutocompleteRenderMode.Off,
		};

	private ConsoleLineReader.AutocompleteColorStyles ResolveAutocompleteColorStyles(bool enabled)
	{
		if (!enabled)
		{
			return ConsoleLineReader.AutocompleteColorStyles.Empty;
		}

		var palette = _options.Output.ResolvePalette();
		return new ConsoleLineReader.AutocompleteColorStyles(
			CommandStyle: palette.AutocompleteCommandStyle,
			ContextStyle: palette.AutocompleteContextStyle,
			ParameterStyle: palette.AutocompleteParameterStyle,
			AmbiguousStyle: palette.AutocompleteAmbiguousStyle,
			ErrorStyle: palette.AutocompleteErrorStyle,
			HintLabelStyle: palette.AutocompleteHintLabelStyle);
	}

	private async ValueTask<ConsoleLineReader.AutocompleteResult?> ResolveAutocompleteAsync(
		ConsoleLineReader.AutocompleteRequest request,
		IReadOnlyList<string> scopeTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		var comparer = _options.Interactive.Autocomplete.CaseSensitive
			? StringComparer.Ordinal
			: StringComparer.OrdinalIgnoreCase;
		var prefixComparison = _options.Interactive.Autocomplete.CaseSensitive
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
		var liveHint = _options.Interactive.Autocomplete.LiveHintEnabled
			? BuildLiveHint(
				matchingRoutes,
				candidates,
				state.CommandPrefix,
				state.CurrentTokenPrefix,
				_options.Interactive.Autocomplete.LiveHintMaxAlternatives)
			: null;
		var discoverableRoutes = ResolveDiscoverableRoutes(
			activeGraph.Routes,
			activeGraph.Contexts,
			scopeTokens,
			prefixComparison);
		var discoverableContexts = ResolveDiscoverableContexts(
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
				_options.Parsing,
				serviceProvider,
				cancellationToken)
			.ConfigureAwait(false);
		var contextCandidates = _options.Interactive.Autocomplete.ShowContextAlternatives
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
		if (!_options.Interactive.Autocomplete.ShowInvalidAlternatives
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
		if (_options.AmbientCommands.ExitCommandEnabled)
		{
			AddAmbientSuggestion(
				suggestions,
				value: "exit",
				description: "Leave interactive mode.",
				currentTokenPrefix,
				comparison);
		}

		if (_options.AmbientCommands.ShowHistoryInHelp)
		{
			AddAmbientSuggestion(
				suggestions,
				value: "history",
				description: "Show command history.",
				currentTokenPrefix,
				comparison);
		}

		if (_options.AmbientCommands.ShowCompleteInHelp)
		{
			AddAmbientSuggestion(
				suggestions,
				value: "complete",
				description: "Query completion provider.",
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
		if (!IsHelpToken(ambientToken))
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
			if (IsContextSuppressedForDiscovery(context, helpPathPrefix, comparison))
			{
				continue;
			}

			if (!MatchesTemplatePrefix(context.Template, helpPathPrefix, comparison, _options.Parsing)
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
				|| IsRouteSuppressedForDiscovery(route.Template, contexts, helpPathPrefix, comparison)
				|| !MatchesTemplatePrefix(route.Template, helpPathPrefix, comparison, _options.Parsing)
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
				|| IsRouteSuppressedForDiscovery(route.Template, contexts, commandPrefix, comparison)
				|| segmentIndex >= route.Template.Segments.Count)
			{
				continue;
			}

			if (!MatchesRoutePrefix(route.Template, commandPrefix, comparison, _options.Parsing))
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
				&& RouteConstraintEvaluator.IsMatch(dynamic, currentTokenPrefix, _options.Parsing))
			{
				hasDynamicOrContextMatch = true;
			}
		}

		foreach (var context in contexts)
		{
			if (IsContextSuppressedForDiscovery(context, commandPrefix, comparison))
			{
				continue;
			}

			if (segmentIndex >= context.Template.Segments.Count
				|| !MatchesContextPrefix(context.Template, commandPrefix, comparison, _options.Parsing))
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
				&& RouteConstraintEvaluator.IsMatch(dynamic, currentTokenPrefix, _options.Parsing))
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
			if (IsContextSuppressedForDiscovery(context, commandPrefix, comparison))
			{
				continue;
			}

			if (!MatchesContextPrefix(context.Template, commandPrefix, comparison, _options.Parsing))
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

		if (RouteConstraintEvaluator.IsMatch(dynamic, currentTokenPrefix, _options.Parsing))
		{
			suggestions.Add(new ConsoleLineReader.AutocompleteSuggestion(
				currentTokenPrefix,
				DisplayText: placeholderValue,
				Description: $"Context [{BuildContextTargetPath(commandPrefix, currentTokenPrefix)}]",
				Kind: ConsoleLineReader.AutocompleteSuggestionKind.Context));
			return;
		}

		if (!_options.Interactive.Autocomplete.ShowInvalidAlternatives)
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

	private List<RouteDefinition> CollectVisibleMatchingRoutes(
		string[] commandPrefix,
		StringComparison prefixComparison,
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<ContextDefinition> contexts)
	{
		var matches = routes
			.Where(route =>
				!route.Command.IsHidden
				&& !IsRouteSuppressedForDiscovery(route.Template, contexts, commandPrefix, prefixComparison)
				&& MatchesRoutePrefix(route.Template, commandPrefix, prefixComparison, _options.Parsing))
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

	private static bool IsGlobalOptionToken(string token) =>
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
				|| IsRouteSuppressedForDiscovery(route.Template, contexts, prefixTokens, comparison)
				|| !TryClassifyTemplateSegment(
					route.Template,
					prefixTokens,
					token,
					comparison,
					_options.Parsing,
					out var routeKind))
			{
				continue;
			}

			routeLiteralMatch |= routeKind == ConsoleLineReader.AutocompleteSuggestionKind.Command;
			routeDynamicMatch |= routeKind == ConsoleLineReader.AutocompleteSuggestionKind.Parameter;
		}

		var contextMatch = contexts.Any(context =>
			!IsContextSuppressedForDiscovery(context, prefixTokens, comparison)
			&&
			TryClassifyTemplateSegment(
				context.Template,
				prefixTokens,
				token,
				comparison,
				_options.Parsing,
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
		if (IsHelpToken(ambientToken)
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

		if (_options.AmbientCommands.ExitCommandEnabled && "exit".StartsWith(token, comparison))
		{
			return true;
		}

		if (_options.AmbientCommands.ShowHistoryInHelp && "history".StartsWith(token, comparison))
		{
			return true;
		}

		return _options.AmbientCommands.ShowCompleteInHelp && "complete".StartsWith(token, comparison);
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

	private static List<TokenSpan> TokenizeInputSpans(string input)
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

	private readonly record struct TokenSpan(string Value, int Start, int End);

	private static List<string> TokenizeInteractiveInput(string input)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return [];
		}

		var tokens = new List<string>();
		var current = new System.Text.StringBuilder();
		char? quote = null;
		foreach (var ch in input)
		{
			if (quote is null && (ch == '"' || ch == '\''))
			{
				quote = ch;
				continue;
			}

			if (quote is not null && ch == quote.Value)
			{
				quote = null;
				continue;
			}

			if (quote is null && char.IsWhiteSpace(ch))
			{
				if (current.Length > 0)
				{
					tokens.Add(current.ToString());
					current.Clear();
				}

				continue;
			}

			current.Append(ch);
		}

		if (current.Length > 0)
		{
			tokens.Add(current.ToString());
		}

		return tokens;
	}
}
