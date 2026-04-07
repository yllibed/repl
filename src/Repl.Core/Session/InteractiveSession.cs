using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Repl.Internal.Options;

namespace Repl;

/// <summary>
/// Encapsulates the interactive REPL session loop and ambient command dispatch.
/// </summary>
internal sealed class InteractiveSession(CoreReplApp app)
{
	internal bool ShouldEnterInteractive(GlobalInvocationOptions globalOptions, bool allowAuto)
	{
		if (globalOptions.InteractivePrevented)
		{
			return false;
		}

		if (globalOptions.InteractiveForced)
		{
			return true;
		}

		return app.OptionsSnapshot.Interactive.InteractivePolicy switch
		{
			InteractivePolicy.Force => true,
			InteractivePolicy.Prevent => false,
			_ => allowAuto,
		};
	}

	internal async ValueTask<int> RunInteractiveSessionAsync(
		IReadOnlyList<string> initialScopeTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		using var runtimeStateScope = app.PushRuntimeState(serviceProvider, isInteractiveSession: true);
		using var cancelHandler = new CancelKeyHandler();
		var scopeTokens = initialScopeTokens.ToList();
		var historyProvider = serviceProvider.GetService(typeof(IHistoryProvider)) as IHistoryProvider;
		string? lastHistoryEntry = null;
		await app.ShellCompletionRuntimeInstance.HandleStartupAsync(serviceProvider, cancellationToken).ConfigureAwait(false);
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
		var effectiveMode = app.Autocomplete.ResolveEffectiveAutocompleteMode(serviceProvider);
		var renderMode = AutocompleteEngine.ResolveAutocompleteRenderMode(effectiveMode);
		var colorStyles = app.Autocomplete.ResolveAutocompleteColorStyles(renderMode == ConsoleLineReader.AutocompleteRenderMode.Rich);
		return await ConsoleLineReader.ReadLineAsync(
				historyProvider,
				effectiveMode == AutocompleteMode.Off
					? null
					: (request, ct) => app.Autocomplete.ResolveAutocompleteAsync(request, scopeTokens, serviceProvider, ct),
				renderMode,
				app.OptionsSnapshot.Interactive.Autocomplete.MaxVisibleSuggestions,
				app.OptionsSnapshot.Interactive.Autocomplete.Presentation,
				app.OptionsSnapshot.Interactive.Autocomplete.LiveHintEnabled
					&& renderMode == ConsoleLineReader.AutocompleteRenderMode.Rich,
				app.OptionsSnapshot.Interactive.Autocomplete.ColorizeInputLine,
				app.OptionsSnapshot.Interactive.Autocomplete.ColorizeHintAndMenu,
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
		var globalOptions = GlobalOptionParser.Parse(invocationTokens, app.OptionsSnapshot.Output, app.OptionsSnapshot.Parsing);
		app.GlobalOptionsSnapshotInstance.Update(globalOptions.CustomGlobalNamedOptions);
		var prefixResolution = app.ResolveUniquePrefixes(globalOptions.RemainingTokens);
		if (prefixResolution.IsAmbiguous)
		{
			var ambiguous = RoutingEngine.CreateAmbiguousPrefixResult(prefixResolution);
			_ = await app.RenderOutputAsync(ambiguous, globalOptions.OutputFormat, cancellationToken, isInteractive: true)
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
		var activeGraph = app.ResolveActiveRoutingGraph();
		if (globalOptions.HelpRequested)
		{
			_ = await app.RenderHelpAsync(globalOptions, cancellationToken).ConfigureAwait(false);
			return;
		}

		var resolution = app.ResolveWithDiagnostics(globalOptions.RemainingTokens, activeGraph.Routes);
		var match = resolution.Match;
		if (match is not null)
		{
			await app.ExecuteMatchedCommandAsync(match, globalOptions, serviceProvider, scopeTokens, cancellationToken).ConfigureAwait(false);
			return;
		}

		var contextMatch = ContextResolver.ResolveExact(activeGraph.Contexts, globalOptions.RemainingTokens, app.OptionsSnapshot.Parsing);
		if (contextMatch is not null)
		{
			var contextValidation = await app.ValidateContextAsync(contextMatch, serviceProvider, cancellationToken)
				.ConfigureAwait(false);
			if (!contextValidation.IsValid)
			{
				_ = await app.RenderOutputAsync(
						contextValidation.Failure,
						globalOptions.OutputFormat,
						cancellationToken,
						isInteractive: true)
					.ConfigureAwait(false);
				return;
			}

			scopeTokens.Clear();
			scopeTokens.AddRange(globalOptions.RemainingTokens);

			if (contextMatch.Context.Banner is { } contextBanner && app.ShouldRenderBanner(globalOptions.OutputFormat))
			{
				await app.InvokeBannerAsync(contextBanner, serviceProvider, cancellationToken).ConfigureAwait(false);
			}

			return;
		}

		var failure = app.CreateRouteResolutionFailureResult(
			tokens: globalOptions.RemainingTokens,
			resolution.ConstraintFailure,
			resolution.MissingArgumentsFailure);
		_ = await app.RenderOutputAsync(
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
	internal async ValueTask<AmbientCommandOutcome> TryHandleAmbientCommandAsync(
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
		if (CoreReplApp.IsHelpToken(token))
		{
			var helpPath = scopeTokens.Concat(inputTokens.Skip(1)).ToArray();
			var helpText = app.BuildHumanHelp(helpPath);
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

		if (app.OptionsSnapshot.AmbientCommands.CustomCommands.TryGetValue(token, out var customAmbient))
		{
			await ExecuteCustomAmbientCommandAsync(customAmbient, serviceProvider, cancellationToken)
				.ConfigureAwait(false);
			return AmbientCommandOutcome.Handled;
		}

		return AmbientCommandOutcome.NotHandled;
	}

	internal static async ValueTask<AmbientCommandOutcome> HandleUpAmbientCommandAsync(
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

	internal async ValueTask<AmbientCommandOutcome> HandleExitAmbientCommandAsync()
	{
		if (app.OptionsSnapshot.AmbientCommands.ExitCommandEnabled)
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
			var configured = app.OptionsSnapshot.Interactive.Autocomplete.Mode;
			var overrideMode = sessionState?.Get<string>(AutocompleteEngine.AutocompleteModeSessionStateKey);
			var effective = app.Autocomplete.ResolveEffectiveAutocompleteMode(serviceProvider);
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

			sessionState?.Set(AutocompleteEngine.AutocompleteModeSessionStateKey, mode.ToString());
			var effective = app.Autocomplete.ResolveEffectiveAutocompleteMode(serviceProvider);
			await ReplSessionIO.Output.WriteLineAsync($"Autocomplete mode set to {mode} (effective: {effective}).")
				.ConfigureAwait(false);
			return AmbientCommandOutcome.Handled;
		}

		await ReplSessionIO.Output.WriteLineAsync("Error: usage: autocomplete [show] | autocomplete mode <off|auto|basic|rich>.")
			.ConfigureAwait(false);
		return AmbientCommandOutcome.HandledError;
	}

	internal async ValueTask<bool> HandleCompletionAmbientCommandAsync(
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
		var resolvedPath = app.ResolveUniquePrefixes(fullCommandPath);
		if (resolvedPath.IsAmbiguous)
		{
			var ambiguous = RoutingEngine.CreateAmbiguousPrefixResult(resolvedPath);
			_ = await app.RenderOutputAsync(ambiguous, requestedFormat: null, cancellationToken).ConfigureAwait(false);
			return false;
		}

		var match = app.Resolve(resolvedPath.Tokens);
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

	private async ValueTask ExecuteCustomAmbientCommandAsync(
		AmbientCommandDefinition command,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var bindingContext = new InvocationBindingContext(
			routeValues: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			namedOptions: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
			positionalArguments: [],
			optionSchema: Internal.Options.OptionSchema.Empty,
			optionCaseSensitivity: app.OptionsSnapshot.Parsing.OptionCaseSensitivity,
			contextValues: [],
			numericFormatProvider: app.OptionsSnapshot.Parsing.NumericFormatProvider ?? CultureInfo.InvariantCulture,
			serviceProvider: serviceProvider,
			interactionOptions: app.OptionsSnapshot.Interaction,
			cancellationToken: cancellationToken);
		var arguments = HandlerArgumentBinder.Bind(command.Handler, bindingContext);
		await CommandInvoker.InvokeAsync(command.Handler, arguments).ConfigureAwait(false);
	}

	internal string[] GetDeepestContextScopePath(IReadOnlyList<string> matchedPathTokens)
	{
		var activeGraph = app.ResolveActiveRoutingGraph();
		var contextMatches = ContextResolver.ResolvePrefixes(activeGraph.Contexts, matchedPathTokens, app.OptionsSnapshot.Parsing);
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
		var basePrompt = app.OptionsSnapshot.Interactive.Prompt;
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

	internal static List<string> TokenizeInteractiveInput(string input)
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
