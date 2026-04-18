using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Repl;

public sealed partial class CoreReplApp
{
	/// <summary>
	/// Runs the app in synchronous mode.
	/// </summary>
	/// <param name="args">Command-line arguments.</param>
	/// <returns>Process exit code.</returns>
	public int Run(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);
#pragma warning disable VSTHRD002 // Sync API intentionally blocks to preserve a conventional Run(...) entrypoint.
		return RunAsync(args).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
	}

	/// <summary>
	/// Runs the app in asynchronous mode.
	/// </summary>
	/// <param name="args">Command-line arguments.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Process exit code.</returns>
	public ValueTask<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(args);
		_ = _description;
		_ = _commands.Count;
		_ = _middleware.Count;
		_ = _options;
		cancellationToken.ThrowIfCancellationRequested();
		return ExecuteCoreAsync(args, _services, cancellationToken: cancellationToken);
	}

	internal ValueTask<int> RunWithServicesAsync(
		string[] args,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken = default) =>
		ExecuteCoreAsync(args, serviceProvider, cancellationToken: cancellationToken);

	/// <summary>
	/// Executes a nested command invocation that preserves the session baseline.
	/// Used by MCP tool calls where the global options from the initial session
	/// must remain in effect even though the sub-invocation tokens don't contain them.
	/// </summary>
	internal ValueTask<int> RunSubInvocationAsync(
		string[] args,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken = default) =>
		ExecuteCoreAsync(args, serviceProvider, isSubInvocation: true, cancellationToken);

	private async ValueTask<int> ExecuteCoreAsync(
		IReadOnlyList<string> args,
		IServiceProvider serviceProvider,
		bool isSubInvocation = false,
		CancellationToken cancellationToken = default)
	{
		_options.Interaction.SetObserver(observer: ExecutionObserver);
		try
		{
			var globalOptions = GlobalOptionParser.Parse(args, _options.Output, _options.Parsing);
			_globalOptionsSnapshot.Update(globalOptions.CustomGlobalNamedOptions); // volatile ref swap — safe under concurrent sub-invocations
			if (!isSubInvocation)
			{
				_globalOptionsSnapshot.SetSessionBaseline();
			}
			using var runtimeStateScope = PushRuntimeState(serviceProvider, isInteractiveSession: false);
			var prefixResolution = ResolveUniquePrefixes(globalOptions.RemainingTokens);
			var resolvedGlobalOptions = globalOptions with { RemainingTokens = prefixResolution.Tokens };
				var ambiguousExitCode = await TryHandleAmbiguousPrefixAsync(
						prefixResolution,
						globalOptions,
						resolvedGlobalOptions,
						serviceProvider,
						cancellationToken)
					.ConfigureAwait(false);
				if (ambiguousExitCode is not null) return ambiguousExitCode.Value;

			var preResolvedRouteResolution = TryPreResolveRouteForBanner(resolvedGlobalOptions);
			if (!ShouldSuppressGlobalBanner(resolvedGlobalOptions, preResolvedRouteResolution?.Match))
			{
				await TryRenderBannerAsync(resolvedGlobalOptions, serviceProvider, cancellationToken).ConfigureAwait(false);
			}

				var preExecutionExitCode = await TryHandlePreExecutionAsync(
						resolvedGlobalOptions,
						serviceProvider,
						cancellationToken)
					.ConfigureAwait(false);
				if (preExecutionExitCode is not null) return preExecutionExitCode.Value;

			var resolution = preResolvedRouteResolution
				?? ResolveWithDiagnostics(resolvedGlobalOptions.RemainingTokens);
			var match = resolution.Match;
				if (match is null)
				{
					return await TryHandleContextDeeplinkAsync(
							resolvedGlobalOptions,
							serviceProvider,
							cancellationToken,
							constraintFailure: resolution.ConstraintFailure,
							missingArgumentsFailure: resolution.MissingArgumentsFailure)
						.ConfigureAwait(false);
				}

			return await ExecuteMatchedCommandAndMaybeEnterInteractiveAsync(
					match,
					resolvedGlobalOptions,
					serviceProvider,
					cancellationToken)
				.ConfigureAwait(false);
		}
		finally
		{
			_options.Interaction.SetObserver(observer: null);
		}
	}

	private async ValueTask<int?> TryHandleAmbiguousPrefixAsync(
		PrefixResolutionResult prefixResolution,
		GlobalInvocationOptions globalOptions,
		GlobalInvocationOptions resolvedGlobalOptions,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (!prefixResolution.IsAmbiguous)
		{
			return null;
		}

		if (!ShouldSuppressGlobalBanner(resolvedGlobalOptions, preResolvedMatch: null))
		{
			await TryRenderBannerAsync(resolvedGlobalOptions, serviceProvider, cancellationToken).ConfigureAwait(false);
		}

		var ambiguous = CreateAmbiguousPrefixResult(prefixResolution);
		_ = await RenderOutputAsync(ambiguous, globalOptions.OutputFormat, cancellationToken)
			.ConfigureAwait(false);
		return 1;
	}

	private static bool ShouldSuppressGlobalBanner(
		GlobalInvocationOptions globalOptions,
		RouteMatch? preResolvedMatch)
	{
		if (globalOptions.HelpRequested || globalOptions.RemainingTokens.Count == 0)
		{
			return false;
		}

		return preResolvedMatch?.Route.Command.IsProtocolPassthrough == true;
	}

	private RouteResolver.RouteResolutionResult? TryPreResolveRouteForBanner(GlobalInvocationOptions globalOptions)
	{
		if (globalOptions.HelpRequested || globalOptions.RemainingTokens.Count == 0)
		{
			return null;
		}

		return ResolveWithDiagnostics(globalOptions.RemainingTokens);
	}

	private async ValueTask<int?> TryHandlePreExecutionAsync(
		GlobalInvocationOptions options,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var completionHandled = await TryHandleCompletionCommandAsync(options, serviceProvider, cancellationToken)
			.ConfigureAwait(false);
		if (completionHandled is not null)
		{
			return completionHandled.Value;
		}

		if (options.HelpRequested)
		{
			var rendered = await RenderHelpAsync(options, cancellationToken).ConfigureAwait(false);
			return rendered ? 0 : 1;
		}

		if (options.RemainingTokens.Count == 0)
		{
			return await HandleEmptyInvocationAsync(options, serviceProvider, cancellationToken)
				.ConfigureAwait(false);
		}

		return await TryHandleAmbientInNonInteractiveAsync(options, serviceProvider, cancellationToken)
			.ConfigureAwait(false);
	}

	private async ValueTask<int> ExecuteMatchedCommandAndMaybeEnterInteractiveAsync(
		RouteMatch match,
		GlobalInvocationOptions globalOptions,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (match.Route.Command.IsProtocolPassthrough
			&& ReplSessionIO.IsHostedSession
			&& !match.Route.Command.SupportsHostedProtocolPassthrough)
		{
			_ = await RenderOutputAsync(
					Results.Error(
						"protocol_passthrough_hosted_not_supported",
						$"Command '{match.Route.Template.Template}' is protocol passthrough and requires a handler parameter of type IReplIoContext in hosted sessions."),
					globalOptions.OutputFormat,
					cancellationToken)
				.ConfigureAwait(false);
			return 1;
		}

		if (match.Route.Command.IsProtocolPassthrough)
		{
			return await ExecuteProtocolPassthroughCommandAsync(match, globalOptions, serviceProvider, cancellationToken)
				.ConfigureAwait(false);
		}

		var (exitCode, enterInteractive) = await ExecuteMatchedCommandAsync(
				match,
				globalOptions,
				serviceProvider,
				scopeTokens: null,
				cancellationToken)
			.ConfigureAwait(false);

		if (enterInteractive || (exitCode == 0 && ShouldEnterInteractive(globalOptions, allowAuto: false)))
		{
			var matchedPathLength = globalOptions.RemainingTokens.Count - match.RemainingTokens.Count;
			var matchedPathTokens = globalOptions.RemainingTokens.Take(matchedPathLength).ToArray();
			var interactiveScope = GetDeepestContextScopePath(matchedPathTokens);
			return await RunInteractiveSessionAsync(interactiveScope, serviceProvider, cancellationToken).ConfigureAwait(false);
		}

		return exitCode;
	}

	private async ValueTask<int> ExecuteProtocolPassthroughCommandAsync(
		RouteMatch match,
		GlobalInvocationOptions globalOptions,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		using var protocolPassthroughScope = ReplSessionIO.PushProtocolPassthrough();

		if (ReplSessionIO.IsSessionActive)
		{
			var (exitCode, _) = await ExecuteMatchedCommandAsync(
					match,
					globalOptions,
					serviceProvider,
					scopeTokens: null,
					cancellationToken)
				.ConfigureAwait(false);
			return exitCode;
		}

		using var protocolScope = ReplSessionIO.SetSession(
			Console.Error,
			Console.In,
			ansiMode: AnsiMode.Never,
			commandOutput: Console.Out,
			error: Console.Error,
			isHostedSession: false);
		var (code, _) = await ExecuteMatchedCommandAsync(
				match,
				globalOptions,
				serviceProvider,
				scopeTokens: null,
				cancellationToken)
			.ConfigureAwait(false);
		return code;
	}

	private async ValueTask<int> HandleEmptyInvocationAsync(
		GlobalInvocationOptions globalOptions,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (ShouldEnterInteractive(globalOptions, allowAuto: true))
		{
			return await RunInteractiveSessionAsync([], serviceProvider, cancellationToken).ConfigureAwait(false);
		}

		var helpText = BuildHumanHelp([]);
		await ReplSessionIO.Output.WriteLineAsync(helpText).ConfigureAwait(false);
		return 0;
	}

	private async ValueTask<int?> TryHandleCompletionCommandAsync(
		GlobalInvocationOptions options,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (options.RemainingTokens.Count == 0
			|| !string.Equals(options.RemainingTokens[0], "complete", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var completed = await HandleCompletionAmbientCommandAsync(
				commandTokens: options.RemainingTokens.Skip(1).ToArray(),
				scopeTokens: [],
				serviceProvider: serviceProvider,
				cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		return completed ? 0 : 1;
	}

	private async ValueTask<int?> TryHandleAmbientInNonInteractiveAsync(
		GlobalInvocationOptions options,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (options.RemainingTokens.Count != 1)
		{
			return null;
		}

		var token = options.RemainingTokens[0];
		AmbientCommandOutcome ambientOutcome;
		if (string.Equals(token, "exit", StringComparison.OrdinalIgnoreCase))
		{
			ambientOutcome = await HandleExitAmbientCommandAsync().ConfigureAwait(false);
		}
		else if (string.Equals(token, "..", StringComparison.Ordinal))
		{
			ambientOutcome = await HandleUpAmbientCommandAsync(scopeTokens: [], isInteractiveSession: false)
				.ConfigureAwait(false);
		}
		else
		{
			return null;
		}

		return ambientOutcome switch
		{
			AmbientCommandOutcome.Exit => 0,
			AmbientCommandOutcome.Handled => 0,
			AmbientCommandOutcome.HandledError => 1,
			_ => null,
		};
	}

	private async ValueTask TryRenderBannerAsync(
		GlobalInvocationOptions globalOptions,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (globalOptions.LogoSuppressed)
		{
			_allBannersSuppressed.Value = true;
		}

		if (_bannerRendered.Value || _allBannersSuppressed.Value || !_options.Output.BannerEnabled)
		{
			return;
		}

		var requestedFormat = string.IsNullOrWhiteSpace(globalOptions.OutputFormat)
			? _options.Output.DefaultFormat
			: globalOptions.OutputFormat;
		if (!_options.Output.BannerFormats.Contains(requestedFormat))
		{
			return;
		}

		var banner = BuildBannerText();
		if (!string.IsNullOrWhiteSpace(banner))
		{
			await ReplSessionIO.Output.WriteLineAsync(banner).ConfigureAwait(false);
		}

		if (_banner is not null)
		{
			await InvokeBannerAsync(_banner, serviceProvider, cancellationToken).ConfigureAwait(false);
		}

		_bannerRendered.Value = true;
	}

	private async ValueTask<int> TryHandleContextDeeplinkAsync(
		GlobalInvocationOptions globalOptions,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken,
		RouteResolver.RouteConstraintFailure? constraintFailure = null,
		RouteResolver.RouteMissingArgumentsFailure? missingArgumentsFailure = null)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		var contextMatch = ContextResolver.ResolveExact(activeGraph.Contexts, globalOptions.RemainingTokens, _options.Parsing);
		if (contextMatch is null)
		{
			var failure = CreateRouteResolutionFailureResult(
				tokens: globalOptions.RemainingTokens,
				constraintFailure,
				missingArgumentsFailure);
			_ = await RenderOutputAsync(failure, globalOptions.OutputFormat, cancellationToken)
				.ConfigureAwait(false);
			return 1;
		}

		var contextValidation = await ValidateContextAsync(contextMatch, serviceProvider, cancellationToken)
			.ConfigureAwait(false);
		if (!contextValidation.IsValid)
		{
			_ = await RenderOutputAsync(
					contextValidation.Failure,
					globalOptions.OutputFormat,
					cancellationToken)
				.ConfigureAwait(false);
			return 1;
		}

		if (!ShouldEnterInteractive(globalOptions, allowAuto: true))
		{
			var helpText = BuildHumanHelp(globalOptions.RemainingTokens);
			await ReplSessionIO.Output.WriteLineAsync(helpText).ConfigureAwait(false);
			return 0;
		}

		return await RunInteractiveSessionAsync(globalOptions.RemainingTokens.ToArray(), serviceProvider, cancellationToken)
			.ConfigureAwait(false);
	}

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Execution path intentionally keeps validation, binding, middleware and rendering in one place.")]
	internal async ValueTask<(int ExitCode, bool EnterInteractive)> ExecuteMatchedCommandAsync(
		RouteMatch match,
		GlobalInvocationOptions globalOptions,
		IServiceProvider serviceProvider,
		List<string>? scopeTokens,
		CancellationToken cancellationToken)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		_options.Interaction.SetPrefilledAnswers(globalOptions.PromptAnswers);
		var commandParsingOptions = BuildEffectiveCommandParsingOptions();
		var optionComparer = commandParsingOptions.OptionCaseSensitivity == ReplCaseSensitivity.CaseInsensitive
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;
		var knownOptionNames = new HashSet<string>(match.Route.OptionSchema.Parameters.Keys, optionComparer);
		if (TryFindGlobalCommandOptionCollision(globalOptions, knownOptionNames, out var collidingOption))
		{
			_ = await RenderOutputAsync(
					Results.Validation($"Ambiguous option '{collidingOption}'. It is defined as both global and command option."),
					globalOptions.OutputFormat,
					cancellationToken)
				.ConfigureAwait(false);
			return (1, false);
		}

		var parsedOptions = InvocationOptionParser.Parse(
			match.RemainingTokens,
			match.Route.OptionSchema,
			commandParsingOptions);
		if (parsedOptions.HasErrors)
		{
			var firstError = parsedOptions.Diagnostics
				.First(diagnostic => diagnostic.Severity == ParseDiagnosticSeverity.Error);
			_ = await RenderOutputAsync(
					Results.Validation(firstError.Message),
					globalOptions.OutputFormat,
					cancellationToken)
				.ConfigureAwait(false);
			return (1, false);
		}
		var matchedPathLength = globalOptions.RemainingTokens.Count - match.RemainingTokens.Count;
		var matchedPathTokens = globalOptions.RemainingTokens.Take(matchedPathLength).ToArray();
		var bindingContext = CreateInvocationBindingContext(
			match,
			parsedOptions,
			globalOptions,
			commandParsingOptions,
			matchedPathTokens,
			activeGraph.Contexts,
			serviceProvider,
			cancellationToken);
		try
		{
			var arguments = HandlerArgumentBinder.Bind(match.Route.Command.Handler, bindingContext);
			var contextFailure = await ValidateContextsForMatchAsync(
					match,
					matchedPathTokens,
					activeGraph.Contexts,
					serviceProvider,
					cancellationToken)
				.ConfigureAwait(false);
			if (contextFailure is not null)
			{
				_ = await RenderOutputAsync(contextFailure, globalOptions.OutputFormat, cancellationToken)
					.ConfigureAwait(false);
				return (1, false);
			}

			await TryRenderCommandBannerAsync(match.Route.Command, globalOptions.OutputFormat, serviceProvider, cancellationToken)
				.ConfigureAwait(false);
				var result = await ExecuteWithMiddlewareAsync(
						match.Route.Command.Handler,
						arguments,
						serviceProvider,
						cancellationToken)
					.ConfigureAwait(false);
				await TryClearProgressAsync(serviceProvider).ConfigureAwait(false);

				if (TupleDecomposer.IsTupleResult(result, out var tuple))
				{
					return await RenderTupleResultAsync(tuple, scopeTokens, globalOptions, cancellationToken)
						.ConfigureAwait(false);
				}

				if (result is EnterInteractiveResult enterInteractive)
				{
					if (enterInteractive.Payload is not null)
					{
						_ = await RenderOutputAsync(enterInteractive.Payload, globalOptions.OutputFormat, cancellationToken, scopeTokens is not null)
							.ConfigureAwait(false);
					}

					return (0, true);
				}

				var normalizedResult = ApplyNavigationResult(result, scopeTokens);
				ExecutionObserver?.OnResult(normalizedResult);
				var rendered = await RenderOutputAsync(normalizedResult, globalOptions.OutputFormat, cancellationToken, scopeTokens is not null)
					.ConfigureAwait(false);
				return (rendered ? ComputeExitCode(normalizedResult) : 1, false);
		}
		catch (OperationCanceledException)
		{
			await TryClearProgressAsync(serviceProvider).ConfigureAwait(false);
			throw;
		}
		catch (InvalidOperationException ex)
		{
			await TryClearProgressAsync(serviceProvider).ConfigureAwait(false);
			_ = await RenderOutputAsync(Results.Validation(ex.Message), globalOptions.OutputFormat, cancellationToken)
				.ConfigureAwait(false);
			return (1, false);
		}
		catch (Exception ex)
		{
			await TryClearProgressAsync(serviceProvider).ConfigureAwait(false);
			var errorMessage = ex is TargetInvocationException { InnerException: not null } tie
				? tie.InnerException?.Message ?? ex.Message
				: ex.Message;
			_ = await RenderOutputAsync(
					Results.Error("execution_error", errorMessage),
					globalOptions.OutputFormat,
					cancellationToken)
				.ConfigureAwait(false);
			return (1, false);
		}
	}

	private static async ValueTask TryClearProgressAsync(IServiceProvider serviceProvider)
	{
		if (serviceProvider.GetService(typeof(IReplInteractionChannel)) is not IReplInteractionChannel interaction)
		{
			return;
		}

		try
		{
			await interaction.ClearProgressAsync().ConfigureAwait(false);
		}
		catch (Exception)
		{
			// Clearing progress is best-effort cleanup and must not hide the primary result.
		}
	}

	private async ValueTask<(int ExitCode, bool EnterInteractive)> RenderTupleResultAsync(
		ITuple tuple,
		List<string>? scopeTokens,
		GlobalInvocationOptions globalOptions,
		CancellationToken cancellationToken)
	{
		var isInteractive = scopeTokens is not null;
		var exitCode = 0;
		var enterInteractive = false;

		for (var i = 0; i < tuple.Length; i++)
		{
			var element = tuple[i];

			// EnterInteractiveResult: extract payload (if any) and signal interactive entry.
			if (element is EnterInteractiveResult eir)
			{
				enterInteractive = true;
				element = eir.Payload;
				if (element is null)
				{
					continue;
				}
			}

			var isLast = i == tuple.Length - 1;

			// Navigation results: only apply navigation on the last element.
			var normalized = element is ReplNavigationResult nav && !isLast
				? nav.Payload
				: isLast
					? ApplyNavigationResult(element, scopeTokens)
					: element;

			ExecutionObserver?.OnResult(normalized);

			var rendered = await RenderOutputAsync(normalized, globalOptions.OutputFormat, cancellationToken, isInteractive)
				.ConfigureAwait(false);

			if (!rendered)
			{
				return (1, false);
			}

			if (isLast)
			{
				exitCode = ComputeExitCode(normalized);
			}
		}

		return (exitCode, enterInteractive);
	}

	private static int ComputeExitCode(object? result)
	{
		if (result is IExitResult exitResult)
		{
			return exitResult.ExitCode;
		}

		if (result is not IReplResult replResult)
		{
			return 0;
		}

		var kind = replResult.Kind.ToLowerInvariant();
		if (kind is "text" or "success")
		{
			return 0;
		}

		if (kind is "error" or "validation" or "not_found")
		{
			return 1;
		}

		return 1;
	}

	internal async ValueTask<bool> RenderOutputAsync(
		object? result,
		string? requestedFormat,
		CancellationToken cancellationToken,
		bool isInteractive = false)
	{
		if (result is IExitResult exitResult)
		{
			if (exitResult.Payload is null)
			{
				return true;
			}

			result = exitResult.Payload;
		}

		var format = string.IsNullOrWhiteSpace(requestedFormat)
			? _options.Output.DefaultFormat
			: requestedFormat;
		if (!_options.Output.Transformers.TryGetValue(format, out var transformer))
		{
			// Unknown format is a user-facing validation issue; avoid silent failures from exception swallowing.
			await ReplSessionIO.Output.WriteLineAsync($"Error: unknown output format '{format}'.").ConfigureAwait(false);
			return false;
		}

		var payload = await transformer.TransformAsync(result, cancellationToken).ConfigureAwait(false);
		payload = TryColorizeStructuredPayload(payload, format, isInteractive);
		if (!string.IsNullOrEmpty(payload))
		{
			await ReplSessionIO.Output.WriteLineAsync(payload).ConfigureAwait(false);
		}

		return true;
	}

	private string TryColorizeStructuredPayload(string payload, string format, bool isInteractive)
	{
		if (string.IsNullOrEmpty(payload)
			|| !isInteractive
			|| !_options.Output.ColorizeStructuredInteractive
			|| !_options.Output.IsAnsiEnabled()
			|| !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
		{
			return payload;
		}

		return JsonAnsiColorizer.Colorize(payload, _options.Output.ResolvePalette());
	}

	internal async ValueTask<bool> RenderHelpAsync(
		GlobalInvocationOptions globalOptions,
		CancellationToken cancellationToken)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		var discoverableRoutes = ResolveDiscoverableRoutes(
			activeGraph.Routes,
			activeGraph.Contexts,
			globalOptions.RemainingTokens,
			StringComparison.OrdinalIgnoreCase);
		var discoverableContexts = ResolveDiscoverableContexts(
			activeGraph.Contexts,
			globalOptions.RemainingTokens,
			StringComparison.OrdinalIgnoreCase);
		var requestedFormat = string.IsNullOrWhiteSpace(globalOptions.OutputFormat)
			? _options.Output.DefaultFormat
			: globalOptions.OutputFormat;
		if (string.Equals(requestedFormat, "human", StringComparison.OrdinalIgnoreCase))
		{
			var helpText = BuildHumanHelp(globalOptions.RemainingTokens);
			await ReplSessionIO.Output.WriteLineAsync(helpText).ConfigureAwait(false);
			return true;
		}

		var machineHelp = HelpTextBuilder.BuildModel(
			discoverableRoutes,
			discoverableContexts,
			globalOptions.RemainingTokens,
			_options.Parsing);
		return await RenderOutputAsync(machineHelp, requestedFormat, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<object?> ExecuteWithMiddlewareAsync(
		Delegate handler,
		object?[] arguments,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		object? result = null;
		var context = new ReplExecutionContext(serviceProvider, cancellationToken);
		var index = -1;

		async ValueTask NextAsync()
		{
			index++;
			if (index == _middleware.Count)
			{
				result = await CommandInvoker
					.InvokeAsync(handler, arguments)
					.ConfigureAwait(false);
				return;
			}

			var middleware = _middleware[index];
			await middleware(context, NextAsync).ConfigureAwait(false);
		}

		await NextAsync().ConfigureAwait(false);
		return result;
	}

	private static object? ApplyNavigationResult(object? result, List<string>? scopeTokens)
	{
		if (result is not ReplNavigationResult navigation)
		{
			return result;
		}

		if (scopeTokens is null)
		{
			return navigation.Payload;
		}

		ApplyNavigation(scopeTokens, navigation);
		return navigation.Payload;
	}

	private static void ApplyNavigation(List<string> scopeTokens, ReplNavigationResult navigation)
	{
		if (navigation.Kind == ReplNavigationKind.Up)
		{
			if (scopeTokens.Count > 0)
			{
				scopeTokens.RemoveAt(scopeTokens.Count - 1);
			}

			return;
		}

		if (!string.IsNullOrWhiteSpace(navigation.TargetPath))
		{
			scopeTokens.Clear();
			scopeTokens.AddRange(
				navigation.TargetPath
					.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
		}
	}

	private InvocationBindingContext CreateInvocationBindingContext(
		RouteMatch match,
		OptionParsingResult parsedOptions,
		GlobalInvocationOptions globalOptions,
		ParsingOptions commandParsingOptions,
		string[] matchedPathTokens,
		IReadOnlyList<ContextDefinition> contexts,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var contextValues = BuildContextHierarchyValues(match.Route.Template, matchedPathTokens, contexts);
		var mergedNamedOptions = MergeNamedOptions(
			parsedOptions.NamedOptions,
			globalOptions.CustomGlobalNamedOptions);
		return new InvocationBindingContext(
			match.Values,
			mergedNamedOptions,
			parsedOptions.PositionalArguments,
			match.Route.OptionSchema,
			commandParsingOptions.OptionCaseSensitivity,
			contextValues,
			_options.Parsing.NumericFormatProvider,
			serviceProvider,
			_options.Interaction,
			cancellationToken);
	}

	private static bool TryFindGlobalCommandOptionCollision(
		GlobalInvocationOptions globalOptions,
		HashSet<string> knownOptionNames,
		out string collidingOption)
	{
		foreach (var globalOption in globalOptions.CustomGlobalNamedOptions.Keys)
		{
			if (!knownOptionNames.Contains(globalOption))
			{
				continue;
			}

			collidingOption = $"--{globalOption}";
			return true;
		}

		collidingOption = string.Empty;
		return false;
	}

	private static IReadOnlyDictionary<string, IReadOnlyList<string>> MergeNamedOptions(
		IReadOnlyDictionary<string, IReadOnlyList<string>> commandNamedOptions,
		IReadOnlyDictionary<string, IReadOnlyList<string>> globalNamedOptions)
	{
		if (globalNamedOptions.Count == 0)
		{
			return commandNamedOptions;
		}

		var merged = new Dictionary<string, IReadOnlyList<string>>(
			commandNamedOptions,
			StringComparer.OrdinalIgnoreCase);
		foreach (var pair in globalNamedOptions)
		{
			if (merged.TryGetValue(pair.Key, out var existing))
			{
				var appended = existing.Concat(pair.Value).ToArray();
				merged[pair.Key] = appended;
				continue;
			}

			merged[pair.Key] = pair.Value;
		}

		return merged;
	}

	private ParsingOptions BuildEffectiveCommandParsingOptions()
	{
		var isInteractiveSession = _runtimeState.Value?.IsInteractiveSession == true;
		return new ParsingOptions
		{
			AllowUnknownOptions = _options.Parsing.AllowUnknownOptions,
			OptionCaseSensitivity = _options.Parsing.OptionCaseSensitivity,
			AllowResponseFiles = !isInteractiveSession && _options.Parsing.AllowResponseFiles,
		};
	}
}
