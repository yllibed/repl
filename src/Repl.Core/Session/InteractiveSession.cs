using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Repl.Internal.Options;

namespace Repl;

/// <summary>
/// Encapsulates the interactive REPL session loop and ambient command dispatch.
/// </summary>
internal sealed class InteractiveSession(CoreReplApp app)
{
	// Ambient command tokens — the single vocabulary consumed by both
	// IsAmbientCommandInvocation and TryHandleAmbientCommandAsync (and the
	// non-interactive ambient path), so classification and dispatch can never
	// drift apart by editing one list and not the other.
	internal const string UpAmbientToken = "..";
	internal const string ExitAmbientToken = "exit";
	internal const string CompleteAmbientToken = "complete";
	internal const string AutocompleteAmbientToken = "autocomplete";
	internal const string HistoryAmbientToken = "history";

	/// <summary>
	/// Loop-stable state shared by every prompt cycle of one interactive session: the mark
	/// emitter, the mutable scope path, and the session-scoped collaborators. Bundled so
	/// the per-cycle methods don't each grow a long positional parameter list.
	/// </summary>
	private sealed record PromptCycleContext(
		ShellIntegrationMarkEmitter Marks,
		List<string> ScopeTokens,
		IHistoryProvider? HistoryProvider,
		IServiceProvider ServiceProvider,
		CancelKeyHandler CancelHandler);

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
		var cycle = new PromptCycleContext(
			Marks: ShellIntegrationMarkEmitter.Create(
				app.OptionsSnapshot.TerminalIntegration,
				app.OptionsSnapshot.Output,
				// Opened at loop scope so the async-local flows into command handlers,
				// where IReplSessionInfo.ShellIntegrationStatus reads it.
				ShellIntegrationStatusAmbient.Open()),
			ScopeTokens: initialScopeTokens.ToList(),
			HistoryProvider: serviceProvider.GetService(typeof(IHistoryProvider)) as IHistoryProvider,
			ServiceProvider: serviceProvider,
			CancelHandler: cancelHandler);
		string? lastHistoryEntry = null;
		await app.ShellCompletionRuntimeInstance.HandleStartupAsync(serviceProvider, cancellationToken).ConfigureAwait(false);
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				var (exit, updatedHistory) = await RunPromptCycleAsync(cycle, lastHistoryEntry, cancellationToken)
					.ConfigureAwait(false);
				lastHistoryEntry = updatedHistory;
				if (exit)
				{
					return 0;
				}
			}
			catch
			{
				// The prompt marks (A/B) may have opened a cycle before the read or the
				// history append failed. Close it with an aborted command-end (no exit
				// code) so the terminal keeps no unterminated segment, then propagate.
				// No-op if the cycle was already closed (ExecuteCommittedInputAsync handles
				// its own exceptions), since the emitter's phase guard drops a second D.
				await TryWriteCommandEndAsync(cycle.Marks, exitCode: null).ConfigureAwait(false);
				throw;
			}
		}
	}

	/// <summary>
	/// Runs one prompt cycle: emits the prompt marks, reads a line, and dispatches it.
	/// Returns whether the session should exit and the updated last-history entry.
	/// </summary>
	private async ValueTask<(bool Exit, string? LastHistoryEntry)> RunPromptCycleAsync(
		PromptCycleContext cycle,
		string? lastHistoryEntry,
		CancellationToken cancellationToken)
	{
		var marks = cycle.Marks;
		var readResult = await ReadInteractiveInputAsync(cycle, cancellationToken).ConfigureAwait(false);
		if (readResult.Escaped)
		{
			await marks.WriteCommandEndAsync(exitCode: null).ConfigureAwait(false);
			await ReplSessionIO.Output.WriteLineAsync().ConfigureAwait(false);
			return (false, lastHistoryEntry); // Esc at bare prompt → fresh line.
		}

		var line = readResult.Line;
		if (line is null)
		{
			await marks.WriteCommandEndAsync(exitCode: null).ConfigureAwait(false);
			return (true, lastHistoryEntry);
		}

		var inputTokens = TokenizeInteractiveInput(line);
		if (inputTokens.Count == 0)
		{
			await marks.WriteCommandEndAsync(exitCode: null).ConfigureAwait(false);
			return (false, lastHistoryEntry);
		}

		lastHistoryEntry = await TryAppendHistoryAsync(
				cycle.HistoryProvider,
				lastHistoryEntry,
				line,
				cancellationToken)
			.ConfigureAwait(false);

		var outcome = await ExecuteCommittedInputAsync(cycle, line, inputTokens, cancellationToken)
			.ConfigureAwait(false);
		return (outcome == AmbientCommandOutcome.Exit, lastHistoryEntry);
	}

	private async ValueTask<ConsoleLineReader.ReadResult> ReadInteractiveInputAsync(
		PromptCycleContext cycle,
		CancellationToken cancellationToken)
	{
		var scopeTokens = cycle.ScopeTokens;
		var serviceProvider = cycle.ServiceProvider;
		var promptText = BuildPrompt(scopeTokens);
		await cycle.Marks.WritePromptStartAsync(promptText).ConfigureAwait(false);
		await ReplSessionIO.Output.WriteAsync(promptText).ConfigureAwait(false);
		await ReplSessionIO.Output.WriteAsync(' ').ConfigureAwait(false);
		await cycle.Marks.WriteInputStartAsync().ConfigureAwait(false);
		var effectiveMode = app.Autocomplete.ResolveEffectiveAutocompleteMode(serviceProvider);
		var renderMode = AutocompleteEngine.ResolveAutocompleteRenderMode(effectiveMode);
		var colorStyles = app.Autocomplete.ResolveAutocompleteColorStyles(renderMode == ConsoleLineReader.AutocompleteRenderMode.Rich);
		return await ConsoleLineReader.ReadLineAsync(
				cycle.HistoryProvider,
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

	private async ValueTask<(AmbientCommandOutcome Outcome, int ExitCode)> DispatchInteractiveCommandAsync(
		CommittedResolution resolution,
		IReadOnlyList<string> inputTokens,
		PromptCycleContext cycle,
		CancellationToken cancellationToken)
	{
		if (resolution.Kind == CommittedKind.Ambient)
		{
			var ambientOutcome = await TryHandleAmbientCommandAsync(
					inputTokens,
					cycle.ScopeTokens,
					cycle.ServiceProvider,
					isInteractiveSession: true,
					cancellationToken)
				.ConfigureAwait(false);
			return (ambientOutcome, ambientOutcome == AmbientCommandOutcome.HandledError ? 1 : 0);
		}

		if (resolution.Kind == CommittedKind.Ambiguous)
		{
			// Globals were already applied in ResolveCommittedInput (before routing);
			// reuse the prefix result from that single resolution — do not re-resolve.
			var ambiguous = RoutingEngine.CreateAmbiguousPrefixResult(resolution.Prefix);
			_ = await app.RenderOutputAsync(ambiguous, resolution.Options.OutputFormat, cancellationToken, isInteractive: true)
				.ConfigureAwait(false);
			return (AmbientCommandOutcome.Handled, 1);
		}

		// Help or Routed: both flow through the command-cancellation scope so Ctrl-C and
		// exit-code computation behave identically; the pre-resolved graph/match is reused.
		var exitCode = await ExecuteWithCancellationAsync(resolution, cycle, cancellationToken)
			.ConfigureAwait(false);
		return (AmbientCommandOutcome.Handled, exitCode);
	}

	private async ValueTask<int> ExecuteWithCancellationAsync(
		CommittedResolution resolution,
		PromptCycleContext cycle,
		CancellationToken cancellationToken)
	{
		using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		SetCommandTokenOnChannel(cycle.ServiceProvider, commandCts.Token);
		cycle.CancelHandler.SetCommandCts(commandCts);

		try
		{
			return await ExecuteInteractiveInputAsync(resolution, cycle, commandCts.Token)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			await ReplSessionIO.Output.WriteLineAsync("Cancelled.").ConfigureAwait(false);
			// 128 + SIGINT(2): the shell convention for an interrupted command, so
			// shell-integration marks decorate it as interrupted rather than failed. An
			// outer-token cancellation (host shutdown) is NOT matched here — it propagates
			// to ExecuteCommittedInputAsync's OCE catch, which closes the cycle with an
			// aborted D (no exit code) rather than a failure.
			return 130;
		}
		finally
		{
			cycle.CancelHandler.SetCommandCts(cts: null);
			SetCommandTokenOnChannel(cycle.ServiceProvider, default);
		}
	}

	/// <summary>
	/// Runs one committed input line inside its shell-integration lifecycle: opens the
	/// output region, dispatches, and guarantees the cycle is closed on every path.
	/// The input is resolved exactly once (<see cref="ResolveCommittedInput"/>) and that
	/// single result drives both the passthrough mark decision and dispatch, so the two
	/// can never disagree across a concurrent routing-graph invalidation.
	/// </summary>
	private async ValueTask<AmbientCommandOutcome> ExecuteCommittedInputAsync(
		PromptCycleContext cycle,
		string line,
		IReadOnlyList<string> inputTokens,
		CancellationToken cancellationToken)
	{
		var marks = cycle.Marks;
		var resolution = ResolveCommittedInput(inputTokens, cycle.ScopeTokens);

		// A protocol-passthrough command turns the output stream into a protocol
		// channel: no mark may precede or trail its payload. A/B were already
		// written around the prompt (before the command was known); the cycle is
		// abandoned silently and the next prompt-start implicitly closes it.
		var isProtocolPassthrough = resolution.IsProtocolPassthrough;
		if (!isProtocolPassthrough)
		{
			await marks.WriteCommandLineAsync(line).ConfigureAwait(false);
			await marks.WriteOutputStartAsync().ConfigureAwait(false);
		}

		AmbientCommandOutcome outcome;
		int exitCode;
		try
		{
			(outcome, exitCode) = await DispatchInteractiveCommandAsync(resolution, inputTokens, cycle, cancellationToken)
				.ConfigureAwait(false);
		}
		catch when (isProtocolPassthrough)
		{
			marks.AbandonCycle();
			throw;
		}
		catch (OperationCanceledException)
		{
			// Host-shutdown / session cancellation: close the cycle without an exit code
			// (aborted form) rather than decorating it as a failure, then propagate.
			await TryWriteCommandEndAsync(marks, exitCode: null).ConfigureAwait(false);
			throw;
		}
		catch
		{
			// Close the lifecycle before the exception propagates so the terminal never
			// keeps an unterminated command segment. Best-effort: a failing mark write
			// (e.g. a torn-down transport) must not replace the original exception.
			await TryWriteCommandEndAsync(marks, exitCode: 1).ConfigureAwait(false);
			throw;
		}

		if (isProtocolPassthrough)
		{
			// Once a passthrough invocation has dispatched, an exit code cannot prove
			// the payload never started (handlers may emit bytes and then fail), so
			// no mark may trail it — whatever the outcome.
			marks.AbandonCycle();
		}
		else
		{
			await marks.WriteCommandEndAsync(exitCode).ConfigureAwait(false);
		}

		return outcome;
	}

	// Best-effort command-end used on exception paths: the original exception is the
	// signal that matters, so a mark-write failure here is swallowed rather than masking it.
	private static async ValueTask TryWriteCommandEndAsync(ShellIntegrationMarkEmitter marks, int? exitCode)
	{
		try
		{
			await marks.WriteCommandEndAsync(exitCode).ConfigureAwait(false);
		}
		catch
		{
			// Intentionally swallowed — see caller.
		}
	}

	private enum CommittedKind
	{
		Ambient,
		Help,
		Ambiguous,
		Routed,
	}

	/// <summary>
	/// The single resolution of one committed input line: whether it is an ambient
	/// command, a help request, an ambiguous prefix, or a resolved route — captured
	/// against one routing-graph snapshot and reused by both the mark decision and dispatch.
	/// Built through the per-kind factories so which fields are populated is structural:
	/// the guarded accessors throw on a kind mismatch instead of null-forgiving reads.
	/// </summary>
	private readonly struct CommittedResolution
	{
		private readonly GlobalInvocationOptions? _options;
		private readonly ActiveRoutingGraph? _graph;
		private readonly PrefixResolutionResult? _prefix;
		private readonly RouteResolver.RouteResolutionResult? _routes;

		private CommittedResolution(
			CommittedKind kind,
			GlobalInvocationOptions? options,
			ActiveRoutingGraph? graph,
			PrefixResolutionResult? prefix,
			RouteResolver.RouteResolutionResult? routes)
		{
			Kind = kind;
			_options = options;
			_graph = graph;
			_prefix = prefix;
			_routes = routes;
		}

		public static CommittedResolution Ambient() =>
			new(CommittedKind.Ambient, options: null, graph: null, prefix: null, routes: null);

		public static CommittedResolution Ambiguous(
			GlobalInvocationOptions options,
			ActiveRoutingGraph graph,
			PrefixResolutionResult prefix) =>
			new(CommittedKind.Ambiguous, options, graph, prefix, routes: null);

		public static CommittedResolution Help(
			GlobalInvocationOptions options,
			ActiveRoutingGraph graph,
			PrefixResolutionResult prefix) =>
			new(CommittedKind.Help, options, graph, prefix, routes: null);

		public static CommittedResolution Routed(
			GlobalInvocationOptions options,
			ActiveRoutingGraph graph,
			PrefixResolutionResult prefix,
			RouteResolver.RouteResolutionResult routes) =>
			new(CommittedKind.Routed, options, graph, prefix, routes);

		public CommittedKind Kind { get; }

		public GlobalInvocationOptions Options =>
			_options ?? throw new InvalidOperationException("An ambient resolution captures no global options.");

		public ActiveRoutingGraph Graph =>
			_graph ?? throw new InvalidOperationException("An ambient resolution captures no routing graph.");

		public PrefixResolutionResult Prefix =>
			_prefix ?? throw new InvalidOperationException("An ambient resolution captures no prefix result.");

		public RouteResolver.RouteResolutionResult Routes =>
			_routes ?? throw new InvalidOperationException($"A {Kind} resolution captures no route match.");

		public bool IsProtocolPassthrough =>
			Kind == CommittedKind.Routed && _routes?.Match?.Route.Command.IsProtocolPassthrough == true;
	}

	/// <summary>
	/// Resolves the committed input once, against a single <see cref="CoreReplApp.ResolveActiveRoutingGraph"/>
	/// snapshot — prefix expansion, help scoping, and the route match all use that one
	/// snapshot, so the passthrough classification and the eventual execution can never
	/// diverge. Ambient classification uses <see cref="IsAmbientCommandInvocation"/>, the
	/// single authority also consulted by <see cref="TryHandleAmbientCommandAsync"/>.
	/// </summary>
	private CommittedResolution ResolveCommittedInput(IReadOnlyList<string> inputTokens, IReadOnlyList<string> scopeTokens)
	{
		if (IsAmbientCommandInvocation(inputTokens))
		{
			// Ambient commands win over routes sharing the same token and produce
			// normal terminal output, never a protocol payload.
			return CommittedResolution.Ambient();
		}

		var invocationTokens = scopeTokens.Concat(inputTokens).ToArray();
		var globalOptions = GlobalOptionParser.Parse(invocationTokens, app.OptionsSnapshot.Output, app.OptionsSnapshot.Parsing);

		// Apply the parsed globals to the snapshot BEFORE resolving routes: module-presence
		// predicates read IGlobalOptionsAccessor during ResolveActiveRoutingGraph, so a
		// per-command global (e.g. `secret --env prod`) must be visible to routing or a
		// gated command looks missing / a passthrough route is misclassified.
		app.GlobalOptionsSnapshotInstance.Update(globalOptions.CustomGlobalNamedOptions);
		var graph = app.ResolveActiveRoutingGraph();

		// Resolve prefixes against the captured graph BEFORE deciding help or matching, so
		// an abbreviation (`ser` -> `server`) is expanded consistently and `ser --help`
		// scopes help to the resolved command — matching the non-interactive path.
		var prefixResolution = app.ResolveUniquePrefixes(globalOptions.RemainingTokens, graph);
		if (prefixResolution.IsAmbiguous)
		{
			return CommittedResolution.Ambiguous(globalOptions, graph, prefixResolution);
		}

		var resolvedOptions = globalOptions with { RemainingTokens = prefixResolution.Tokens };
		if (resolvedOptions.HelpRequested)
		{
			return CommittedResolution.Help(resolvedOptions, graph, prefixResolution);
		}

		var routes = app.ResolveWithDiagnostics(resolvedOptions.RemainingTokens, graph.Routes);
		return CommittedResolution.Routed(resolvedOptions, graph, prefixResolution, routes);
	}

	/// <summary>
	/// Single authority for "is this input handled as an ambient command?", consulted by
	/// both <see cref="ResolveCommittedInput"/> and the guard in
	/// <see cref="TryHandleAmbientCommandAsync"/> so the two can never disagree.
	/// </summary>
	private bool IsAmbientCommandInvocation(IReadOnlyList<string> inputTokens)
	{
		if (inputTokens.Count == 0)
		{
			return false;
		}

		var token = inputTokens[0];
		return CoreReplApp.IsHelpToken(token)
			|| (inputTokens.Count == 1 && string.Equals(token, UpAmbientToken, StringComparison.Ordinal))
			|| (inputTokens.Count == 1 && string.Equals(token, ExitAmbientToken, StringComparison.OrdinalIgnoreCase))
			|| string.Equals(token, CompleteAmbientToken, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(token, AutocompleteAmbientToken, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(token, HistoryAmbientToken, StringComparison.OrdinalIgnoreCase)
			|| app.OptionsSnapshot.AmbientCommands.CustomCommands.ContainsKey(token);
	}

	private static void SetCommandTokenOnChannel(IServiceProvider serviceProvider, CancellationToken ct)
	{
		if (serviceProvider.GetService(typeof(IReplInteractionChannel)) is ICommandTokenReceiver receiver)
		{
			receiver.SetCommandToken(ct);
		}
	}

	private async ValueTask<int> ExecuteInteractiveInputAsync(
		CommittedResolution committed,
		PromptCycleContext cycle,
		CancellationToken cancellationToken)
	{
		var globalOptions = committed.Options;
		if (globalOptions.HelpRequested)
		{
			var rendered = await app.RenderHelpAsync(globalOptions, cancellationToken).ConfigureAwait(false);
			return rendered ? 0 : 1;
		}

		// Reuse the single routing-graph snapshot and route resolution captured in
		// ResolveCommittedInput — never re-resolve here (that reopened a TOCTOU window
		// against concurrent routing-graph invalidation).
		var activeGraph = committed.Graph;
		var resolution = committed.Routes;
		var match = resolution.Match;
		if (match is not null)
		{
			if (match.Route.Command.IsProtocolPassthrough)
			{
				// Same execution contract as the CLI one-shot path — hosted-capability guard,
				// protocol-passthrough scope, and stream isolation — so a handler probing
				// IsProtocolPassthrough observes the same value in both modes.
				return await app.ExecuteProtocolPassthroughCommandAsync(match, globalOptions, cycle.ServiceProvider, cancellationToken)
					.ConfigureAwait(false);
			}

			var (exitCode, _) = await app.ExecuteMatchedCommandAsync(match, globalOptions, cycle.ServiceProvider, cycle.ScopeTokens, cancellationToken).ConfigureAwait(false);
			return exitCode;
		}

		return await HandleUnmatchedInteractiveInputAsync(activeGraph, resolution, globalOptions, cycle, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Handles a committed input that matched no route: context navigation when the tokens
	/// name a context, a route-resolution failure otherwise.
	/// </summary>
	private async ValueTask<int> HandleUnmatchedInteractiveInputAsync(
		ActiveRoutingGraph activeGraph,
		RouteResolver.RouteResolutionResult resolution,
		GlobalInvocationOptions globalOptions,
		PromptCycleContext cycle,
		CancellationToken cancellationToken)
	{
		var serviceProvider = cycle.ServiceProvider;
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
				return 1;
			}

			cycle.ScopeTokens.Clear();
			cycle.ScopeTokens.AddRange(globalOptions.RemainingTokens);

			if (contextMatch.Context.Banner is { } contextBanner && app.ShouldRenderBanner(globalOptions.OutputFormat))
			{
				await app.InvokeBannerAsync(contextBanner, serviceProvider, cancellationToken).ConfigureAwait(false);
			}

			return 0;
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
		return 1;
	}

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Ambient command routing keeps dispatch table explicit and easy to scan.")]
	internal async ValueTask<AmbientCommandOutcome> TryHandleAmbientCommandAsync(
		IReadOnlyList<string> inputTokens,
		List<string> scopeTokens,
		IServiceProvider serviceProvider,
		bool isInteractiveSession,
		CancellationToken cancellationToken)
	{
		if (!IsAmbientCommandInvocation(inputTokens))
		{
			// Single classification authority (shared with ResolveCommittedInput): if the
			// input is not an ambient command, none of the branches below would match it.
			return AmbientCommandOutcome.NotHandled;
		}

		var token = inputTokens[0];
		if (CoreReplApp.IsHelpToken(token))
		{
			var helpTokens = scopeTokens.Concat(inputTokens.Skip(1)).ToArray();
			var globalOptions = GlobalOptionParser.Parse(
				helpTokens,
				app.OptionsSnapshot.Output,
				app.OptionsSnapshot.Parsing);
			var helpRendered = await app.RenderHelpAsync(globalOptions, cancellationToken).ConfigureAwait(false);
			return helpRendered ? AmbientCommandOutcome.Handled : AmbientCommandOutcome.HandledError;
		}

		if (inputTokens.Count == 1 && string.Equals(token, UpAmbientToken, StringComparison.Ordinal))
		{
			return await HandleUpAmbientCommandAsync(scopeTokens, isInteractiveSession).ConfigureAwait(false);
		}

		if (inputTokens.Count == 1 && string.Equals(token, ExitAmbientToken, StringComparison.OrdinalIgnoreCase))
		{
			return await HandleExitAmbientCommandAsync().ConfigureAwait(false);
		}

		if (string.Equals(token, CompleteAmbientToken, StringComparison.OrdinalIgnoreCase))
		{
			var completionSucceeded = await HandleCompletionAmbientCommandAsync(
					inputTokens.Skip(1).ToArray(),
					scopeTokens,
					serviceProvider,
					cancellationToken)
				.ConfigureAwait(false);
			return completionSucceeded ? AmbientCommandOutcome.Handled : AmbientCommandOutcome.HandledError;
		}

		if (string.Equals(token, AutocompleteAmbientToken, StringComparison.OrdinalIgnoreCase))
		{
			return await HandleAutocompleteAmbientCommandAsync(
					inputTokens.Skip(1).ToArray(),
					serviceProvider,
					isInteractiveSession)
				.ConfigureAwait(false);
		}

		if (string.Equals(token, HistoryAmbientToken, StringComparison.OrdinalIgnoreCase))
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
			implicitServiceParameters: app.ImplicitServiceParameters,
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

	// List<string> rather than IReadOnlyList: the only caller passes the cycle's scope list
	// and CA1859 insists on the concrete type for the string.Join fast path.
	private string BuildPrompt(List<string> scopeTokens)
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
