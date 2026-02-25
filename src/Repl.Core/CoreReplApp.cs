using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Globalization;

namespace Repl;

/// <summary>
/// Entry point for configuring and running a REPL application.
/// </summary>
public sealed class CoreReplApp : ICoreReplApp
{
	private const string AutocompleteModeSessionStateKey = "__repl.autocomplete.mode";

	private readonly List<CommandBuilder> _commands = [];
	private readonly List<ContextDefinition> _contexts = [];
	private readonly List<RouteDefinition> _routes = [];
	private readonly List<Func<ReplExecutionContext, ReplNext, ValueTask>> _middleware = [];
	private readonly ReplOptions _options = new();
	private readonly DefaultServiceProvider _services;
	private string? _description;
	private Delegate? _banner;
	private readonly AsyncLocal<bool> _bannerRendered = new();
	private readonly AsyncLocal<bool> _allBannersSuppressed = new();

	internal ReplOptions OptionsSnapshot => _options;
	internal IReplExecutionObserver? ExecutionObserver { get; set; }

	private CoreReplApp()
	{
		_options.Output.SetHostAnsiSupportResolver(() => _options.Capabilities.SupportsAnsi);
		_services = CreateDefaultServiceProvider();
	}

	/// <summary>
	/// Creates a dependency-free REPL application instance.
	/// </summary>
	/// <returns>A new <see cref="CoreReplApp"/> instance.</returns>
	public static CoreReplApp Create() => new();

	/// <summary>
	/// Sets an application description for discovery and banner usage.
	/// </summary>
	/// <param name="text">Description text.</param>
	/// <returns>The same app instance.</returns>
	public CoreReplApp WithDescription(string text)
	{
		_description = string.IsNullOrWhiteSpace(text)
			? throw new ArgumentException("Description cannot be empty.", nameof(text))
			: text;
		return this;
	}

	/// <summary>
	/// Registers a banner delegate displayed at startup after the header line.
	/// Unlike <see cref="WithDescription"/>, which is structural metadata visible in help and documentation,
	/// banners are display-only messages that appear at runtime.
	/// </summary>
	/// <param name="bannerProvider">Banner delegate with injectable parameters.</param>
	/// <returns>The same app instance.</returns>
	public CoreReplApp WithBanner(Delegate bannerProvider)
	{
		ArgumentNullException.ThrowIfNull(bannerProvider);
		_banner = bannerProvider;
		return this;
	}

	/// <summary>
	/// Registers a static banner string displayed at startup after the header line.
	/// Unlike <see cref="WithDescription"/>, which is structural metadata visible in help and documentation,
	/// banners are display-only messages that appear at runtime.
	/// </summary>
	/// <param name="text">Banner text.</param>
	/// <returns>The same app instance.</returns>
	public CoreReplApp WithBanner(string text)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(text);
		_banner = () => text;
		return this;
	}

	/// <summary>
	/// Registers middleware in the execution pipeline.
	/// </summary>
	/// <param name="middleware">Middleware delegate.</param>
	/// <returns>The same app instance.</returns>
	public CoreReplApp Use(Func<ReplExecutionContext, ReplNext, ValueTask> middleware)
	{
		ArgumentNullException.ThrowIfNull(middleware);
		_middleware.Add(middleware);
		return this;
	}

	/// <summary>
	/// Configures application options.
	/// </summary>
	/// <param name="configure">Options callback.</param>
	/// <returns>The same app instance.</returns>
	public CoreReplApp Options(Action<ReplOptions> configure)
	{
		ArgumentNullException.ThrowIfNull(configure);
		configure(_options);
		return this;
	}

	/// <summary>
	/// Maps a route and command handler.
	/// </summary>
	/// <param name="route">Route template.</param>
	/// <param name="handler">Handler delegate.</param>
	/// <returns>A command builder for metadata configuration.</returns>
	public CommandBuilder Map(string route, Delegate handler)
	{
		route = string.IsNullOrWhiteSpace(route)
			? throw new ArgumentException("Route cannot be empty.", nameof(route))
			: route;
		ArgumentNullException.ThrowIfNull(handler);

		var command = new CommandBuilder(route, handler);
		ApplyMetadataFromAttributes(command, handler);
		var parsedTemplate = RouteTemplateParser.Parse(route, _options.Parsing);
		var template = InferRouteConstraintsFromHandler(parsedTemplate, handler);
		RouteConfigurationValidator.ValidateUnique(
			template,
			_routes.Select(existingRoute => existingRoute.Template));

		_commands.Add(command);
		var routeDefinition = new RouteDefinition(template, command);
		_routes.Add(routeDefinition);
		return command;
	}

	private static RouteTemplate InferRouteConstraintsFromHandler(RouteTemplate template, Delegate handler)
	{
		var handlerParameters = handler.Method
			.GetParameters()
			.Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
			.ToDictionary(parameter => parameter.Name!, StringComparer.OrdinalIgnoreCase);
		if (handlerParameters.Count == 0)
		{
			return template;
		}

		var inferredSegments = template.Segments
			.Select(segment =>
			{
				if (segment is not DynamicRouteSegment dynamic
					|| dynamic.ConstraintKind != RouteConstraintKind.String)
				{
					return segment;
				}

				if (!handlerParameters.TryGetValue(dynamic.Name, out var parameter))
				{
					return segment;
				}

				return TryInferConstraintFromParameterType(parameter.ParameterType, out var inferredConstraint)
					? new DynamicRouteSegment(dynamic.RawText, dynamic.Name, inferredConstraint, isOptional: dynamic.IsOptional)
					: segment;
			})
			.ToArray();
		return new RouteTemplate(template.Template, inferredSegments);
	}

	private static bool TryInferConstraintFromParameterType(
		Type parameterType,
		out RouteConstraintKind constraintKind)
	{
		var effectiveType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
		constraintKind = effectiveType switch
		{
			_ when effectiveType == typeof(Uri) => RouteConstraintKind.Uri,
			_ when effectiveType == typeof(DateOnly) => RouteConstraintKind.Date,
			_ when effectiveType == typeof(DateTime) => RouteConstraintKind.DateTime,
			_ when effectiveType == typeof(TimeOnly) => RouteConstraintKind.Time,
			_ when effectiveType == typeof(DateTimeOffset) => RouteConstraintKind.DateTimeOffset,
			_ when effectiveType == typeof(TimeSpan) => RouteConstraintKind.TimeSpan,
			_ => RouteConstraintKind.String,
		};
		return constraintKind != RouteConstraintKind.String;
	}

	/// <summary>
	/// Creates a top-level context segment and configures nested routes.
	/// </summary>
	/// <param name="segment">Context segment.</param>
	/// <param name="configure">Nested configuration callback.</param>
	/// <param name="validation">Optional scope validation delegate.</param>
	/// <returns>The same app instance.</returns>
	public CoreReplApp Context(string segment, Action<ICoreReplApp> configure, Delegate? validation = null)
	{
		segment = string.IsNullOrWhiteSpace(segment)
			? throw new ArgumentException("Segment cannot be empty.", nameof(segment))
			: segment;
		ArgumentNullException.ThrowIfNull(configure);
		var contextDescription = configure.Method.GetCustomAttribute<DescriptionAttribute>()?.Description;
		RegisterContext(segment, validation, contextDescription);

		var scopedMap = new ScopedMap(this, segment);
		configure(scopedMap);
		return this;
	}

	/// <summary>
	/// Creates a top-level context segment and configures nested routes.
	/// Compatibility overload for <see cref="IReplMap"/> callbacks.
	/// </summary>
	public CoreReplApp Context(string segment, Action<IReplMap> configure, Delegate? validation = null)
	{
		segment = string.IsNullOrWhiteSpace(segment)
			? throw new ArgumentException("Segment cannot be empty.", nameof(segment))
			: segment;
		ArgumentNullException.ThrowIfNull(configure);
		var contextDescription = configure.Method.GetCustomAttribute<DescriptionAttribute>()?.Description;
		RegisterContext(segment, validation, contextDescription);

		var scopedMap = new ScopedMap(this, segment);
		configure(scopedMap);
		return this;
	}

	/// <summary>
	/// Maps a module resolved through runtime activation.
	/// </summary>
	/// <typeparam name="TModule">Module type.</typeparam>
	/// <returns>The same app instance.</returns>
	public CoreReplApp MapModule<
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
		TModule>()
		where TModule : class, IReplModule
	{
		var module = _services.GetService(typeof(TModule)) as TModule
			?? Activator.CreateInstance(typeof(TModule)) as TModule
			?? throw new InvalidOperationException(
				$"Unable to activate module '{typeof(TModule).FullName}'. Provide a parameterless constructor or map an instance.");
		return MapModule(module);
	}

	/// <summary>
	/// Maps a module instance.
	/// </summary>
	/// <param name="module">Module instance.</param>
	/// <returns>The same app instance.</returns>
	public CoreReplApp MapModule(IReplModule module)
	{
		ArgumentNullException.ThrowIfNull(module);
		module.Map(this);
		return this;
	}

	ICoreReplApp ICoreReplApp.Context(string segment, Action<ICoreReplApp> configure, Delegate? validation) =>
		Context(segment, configure, validation);

	ICoreReplApp ICoreReplApp.MapModule(IReplModule module) => MapModule(module);

	ICoreReplApp ICoreReplApp.WithBanner(Delegate bannerProvider) => WithBanner(bannerProvider);

	ICoreReplApp ICoreReplApp.WithBanner(string text) => WithBanner(text);

	IReplMap IReplMap.Context(string segment, Action<IReplMap> configure, Delegate? validation) =>
		Context(segment, configure, validation);

	IReplMap IReplMap.MapModule(IReplModule module) => MapModule(module);

	IReplMap IReplMap.WithBanner(Delegate bannerProvider) => WithBanner(bannerProvider);

	IReplMap IReplMap.WithBanner(string text) => WithBanner(text);

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
		return ExecuteCoreAsync(args, _services, cancellationToken);
	}

	internal RouteMatch? Resolve(IReadOnlyList<string> inputTokens) =>
		RouteResolver.Resolve(_routes, inputTokens, _options.Parsing);

	internal RouteResolver.RouteResolutionResult ResolveWithDiagnostics(IReadOnlyList<string> inputTokens) =>
		RouteResolver.ResolveWithDiagnostics(_routes, inputTokens, _options.Parsing);

	internal ValueTask<int> RunWithServicesAsync(
		string[] args,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken = default) =>
		ExecuteCoreAsync(args, serviceProvider, cancellationToken);

	private async ValueTask<int> ExecuteCoreAsync(
		IReadOnlyList<string> args,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		_options.Interaction.SetObserver(observer: ExecutionObserver);
		try
		{
			var globalOptions = GlobalOptionParser.Parse(args, _options.Output);
			await TryRenderBannerAsync(globalOptions, serviceProvider, cancellationToken).ConfigureAwait(false);
			var prefixResolution = ResolveUniquePrefixes(globalOptions.RemainingTokens);
			if (prefixResolution.IsAmbiguous)
			{
				var ambiguous = CreateAmbiguousPrefixResult(prefixResolution);
				_ = await RenderOutputAsync(ambiguous, globalOptions.OutputFormat, cancellationToken)
					.ConfigureAwait(false);
				return 1;
			}

			var resolvedGlobalOptions = globalOptions with { RemainingTokens = prefixResolution.Tokens };
			var preExecutionExitCode = await TryHandlePreExecutionAsync(
					resolvedGlobalOptions,
					serviceProvider,
					cancellationToken)
				.ConfigureAwait(false);
			if (preExecutionExitCode is not null)
			{
				return preExecutionExitCode.Value;
			}

			var resolution = ResolveWithDiagnostics(resolvedGlobalOptions.RemainingTokens);
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
		var exitCode = await ExecuteMatchedCommandAsync(
				match,
				globalOptions,
				serviceProvider,
				scopeTokens: null,
				cancellationToken)
			.ConfigureAwait(false);
		if (exitCode != 0 || !ShouldEnterInteractive(globalOptions, allowAuto: false))
		{
			return exitCode;
		}

		var matchedPathLength = globalOptions.RemainingTokens.Count - match.RemainingTokens.Count;
		var matchedPathTokens = globalOptions.RemainingTokens.Take(matchedPathLength).ToArray();
		var interactiveScope = GetDeepestContextScopePath(matchedPathTokens);
		return await RunInteractiveSessionAsync(interactiveScope, serviceProvider, cancellationToken).ConfigureAwait(false);
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
		if (!string.Equals(requestedFormat, "human", StringComparison.OrdinalIgnoreCase))
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
		var contextMatch = ContextResolver.ResolveExact(_contexts, globalOptions.RemainingTokens, _options.Parsing);
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

	private async ValueTask<int> ExecuteMatchedCommandAsync(
		RouteMatch match,
		GlobalInvocationOptions globalOptions,
		IServiceProvider serviceProvider,
		List<string>? scopeTokens,
		CancellationToken cancellationToken)
	{
		_options.Interaction.SetPrefilledAnswers(globalOptions.PromptAnswers);
		var parsedOptions = InvocationOptionParser.Parse(match.RemainingTokens);
		var matchedPathLength = globalOptions.RemainingTokens.Count - match.RemainingTokens.Count;
		var matchedPathTokens = globalOptions.RemainingTokens.Take(matchedPathLength).ToArray();
		var bindingContext = CreateInvocationBindingContext(
			match,
			parsedOptions,
			matchedPathTokens,
			serviceProvider,
			cancellationToken);
		try
		{
			var arguments = HandlerArgumentBinder.Bind(match.Route.Command.Handler, bindingContext);
			var contextFailure = await ValidateContextsForMatchAsync(match, matchedPathTokens, serviceProvider, cancellationToken)
				.ConfigureAwait(false);
			if (contextFailure is not null)
			{
				_ = await RenderOutputAsync(contextFailure, globalOptions.OutputFormat, cancellationToken)
					.ConfigureAwait(false);
				return 1;
			}

			await TryRenderCommandBannerAsync(match.Route.Command, globalOptions.OutputFormat, serviceProvider, cancellationToken)
				.ConfigureAwait(false);
				var result = await ExecuteWithMiddlewareAsync(
						match.Route.Command.Handler,
						arguments,
						serviceProvider,
						cancellationToken)
					.ConfigureAwait(false);
				var normalizedResult = ApplyNavigationResult(result, scopeTokens);
				ExecutionObserver?.OnResult(normalizedResult);
				var rendered = await RenderOutputAsync(normalizedResult, globalOptions.OutputFormat, cancellationToken, scopeTokens is not null)
					.ConfigureAwait(false);
				return rendered ? ComputeExitCode(normalizedResult) : 1;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (InvalidOperationException ex)
		{
			_ = await RenderOutputAsync(Results.Validation(ex.Message), globalOptions.OutputFormat, cancellationToken)
				.ConfigureAwait(false);
			return 1;
		}
		catch (Exception ex)
		{
			var errorMessage = ex is TargetInvocationException { InnerException: not null } tie
				? tie.InnerException?.Message ?? ex.Message
				: ex.Message;
			_ = await RenderOutputAsync(
					Results.Error("execution_error", errorMessage),
					globalOptions.OutputFormat,
					cancellationToken)
				.ConfigureAwait(false);
			return 1;
		}
	}

	private static int ComputeExitCode(object? result)
	{
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

	private async ValueTask<bool> RenderOutputAsync(
		object? result,
		string? requestedFormat,
		CancellationToken cancellationToken,
		bool isInteractive = false)
	{
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

	private async ValueTask<bool> RenderHelpAsync(
		GlobalInvocationOptions globalOptions,
		CancellationToken cancellationToken)
	{
		var requestedFormat = string.IsNullOrWhiteSpace(globalOptions.OutputFormat)
			? _options.Output.DefaultFormat
			: globalOptions.OutputFormat;
		if (string.Equals(requestedFormat, "human", StringComparison.OrdinalIgnoreCase))
		{
			var helpText = BuildHumanHelp(globalOptions.RemainingTokens);
			await ReplSessionIO.Output.WriteLineAsync(helpText).ConfigureAwait(false);
			return true;
		}

		var machineHelp = HelpTextBuilder.BuildModel(_routes, _contexts, globalOptions.RemainingTokens, _options.Parsing);
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
		using var cancelHandler = new CancelKeyHandler();
		var scopeTokens = initialScopeTokens.ToList();
		var historyProvider = serviceProvider.GetService(typeof(IHistoryProvider)) as IHistoryProvider;
		string? lastHistoryEntry = null;
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
		if (globalOptions.HelpRequested)
		{
			_ = await RenderHelpAsync(globalOptions, cancellationToken).ConfigureAwait(false);
			return;
		}

		var resolution = ResolveWithDiagnostics(globalOptions.RemainingTokens);
		var match = resolution.Match;
		if (match is not null)
		{
			await ExecuteMatchedCommandAsync(match, globalOptions, serviceProvider, scopeTokens, cancellationToken).ConfigureAwait(false);
			return;
		}

		var contextMatch = ContextResolver.ResolveExact(_contexts, globalOptions.RemainingTokens, _options.Parsing);
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
		var contextMatches = ContextResolver.ResolvePrefixes(_contexts, matchedPathTokens, _options.Parsing);
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
		var comparer = _options.Interactive.Autocomplete.CaseSensitive
			? StringComparer.Ordinal
			: StringComparer.OrdinalIgnoreCase;
		var prefixComparison = _options.Interactive.Autocomplete.CaseSensitive
			? StringComparison.Ordinal
			: StringComparison.OrdinalIgnoreCase;
		var state = ResolveAutocompleteState(request, scopeTokens, prefixComparison);
		var matchingRoutes = CollectVisibleMatchingRoutes(state.CommandPrefix, prefixComparison);
		var candidates = await CollectAutocompleteSuggestionsAsync(
				matchingRoutes,
				state.CommandPrefix,
				state.CurrentTokenPrefix,
				scopeTokens.Count,
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
		var tokenClassifications = BuildTokenClassifications(request.Input, scopeTokens, prefixComparison);
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
		StringComparison comparison)
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
				comparison))
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
			? CollectContextAutocompleteCandidates(commandPrefix, currentTokenPrefix, prefixComparison)
			: [];
		var ambientCandidates = commandPrefix.Length == scopeTokenCount
			? CollectAmbientAutocompleteCandidates(currentTokenPrefix, prefixComparison)
			: [];
		var ambientContinuationCandidates = CollectAmbientContinuationAutocompleteCandidates(
			commandPrefix,
			currentTokenPrefix,
			scopeTokenCount,
			prefixComparison);

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
		StringComparison comparison)
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
		var suggestions = CollectHelpPathAutocompleteCandidates(helpPathPrefix, currentTokenPrefix, comparison);
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
		StringComparison comparison)
	{
		var suggestions = new List<ConsoleLineReader.AutocompleteSuggestion>();
		var segmentIndex = helpPathPrefix.Length;
		foreach (var context in _contexts)
		{
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

		foreach (var route in _routes)
		{
			if (route.Command.IsHidden
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

	private bool ShouldAdvanceToNextToken(
		string[] commandPrefix,
		string currentTokenPrefix,
		int replaceStart,
		int replaceLength,
		int cursor,
		StringComparison comparison)
	{
		if (string.IsNullOrEmpty(currentTokenPrefix) || cursor != replaceStart + replaceLength)
		{
			return false;
		}

		var segmentIndex = commandPrefix.Length;
		var hasLiteralMatch = false;
		var hasDynamicOrContextMatch = false;
		foreach (var route in _routes)
		{
			if (route.Command.IsHidden || segmentIndex >= route.Template.Segments.Count)
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

		foreach (var context in _contexts)
		{
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
		StringComparison comparison)
	{
		var suggestions = new List<ConsoleLineReader.AutocompleteSuggestion>();
		var segmentIndex = commandPrefix.Length;
		foreach (var context in _contexts)
		{
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
		StringComparison prefixComparison)
	{
		var matches = _routes
			.Where(route =>
				!route.Command.IsHidden
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
		StringComparison comparison)
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

	private ConsoleLineReader.AutocompleteSuggestionKind ClassifyToken(
		string[] prefixTokens,
		string token,
		StringComparison comparison,
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
		foreach (var route in _routes)
		{
			if (route.Command.IsHidden
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

		var contextMatch = _contexts.Exists(context =>
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

	private sealed class ScopedMap(CoreReplApp app, string prefix) : ICoreReplApp
	{
		private readonly CoreReplApp _app = app;
		private readonly string _prefix = prefix;

		public CommandBuilder Map(string route, Delegate handler)
		{
			route = string.IsNullOrWhiteSpace(route)
				? throw new ArgumentException("Route cannot be empty.", nameof(route))
				: route;

			var fullRoute = string.Concat(_prefix, " ", route).Trim();
			return _app.Map(fullRoute, handler);
		}

		public ICoreReplApp Context(string segment, Action<ICoreReplApp> configure, Delegate? validation = null)
		{
			segment = string.IsNullOrWhiteSpace(segment)
				? throw new ArgumentException("Segment cannot be empty.", nameof(segment))
				: segment;
			ArgumentNullException.ThrowIfNull(configure);

			var childPrefix = string.Concat(_prefix, " ", segment).Trim();
			var contextDescription = configure.Method.GetCustomAttribute<DescriptionAttribute>()?.Description;
			_app.RegisterContext(childPrefix, validation, contextDescription);
			var childMap = new ScopedMap(_app, childPrefix);
			configure(childMap);
			return this;
		}

		public ICoreReplApp MapModule(IReplModule module)
		{
			ArgumentNullException.ThrowIfNull(module);
			module.Map(this);
			return this;
		}

		IReplMap IReplMap.Context(string segment, Action<IReplMap> configure, Delegate? validation)
		{
			segment = string.IsNullOrWhiteSpace(segment)
				? throw new ArgumentException("Segment cannot be empty.", nameof(segment))
				: segment;
			ArgumentNullException.ThrowIfNull(configure);

			var childPrefix = string.Concat(_prefix, " ", segment).Trim();
			var contextDescription = configure.Method.GetCustomAttribute<DescriptionAttribute>()?.Description;
			_app.RegisterContext(childPrefix, validation, contextDescription);
			var childMap = new ScopedMap(_app, childPrefix);
			configure(childMap);
			return this;
		}

		IReplMap IReplMap.MapModule(IReplModule module) => MapModule(module);

		ICoreReplApp ICoreReplApp.WithBanner(Delegate bannerProvider)
		{
			SetBannerOnContext(bannerProvider);
			return this;
		}

		IReplMap IReplMap.WithBanner(Delegate bannerProvider)
		{
			SetBannerOnContext(bannerProvider);
			return this;
		}

		ICoreReplApp ICoreReplApp.WithBanner(string text)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(text);
			SetBannerOnContext(() => text);
			return this;
		}

		IReplMap IReplMap.WithBanner(string text)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(text);
			SetBannerOnContext(() => text);
			return this;
		}

		private void SetBannerOnContext(Delegate bannerProvider)
		{
			ArgumentNullException.ThrowIfNull(bannerProvider);
			var context = _app._contexts.LastOrDefault(c =>
				string.Equals(c.Template.Template, _prefix, StringComparison.OrdinalIgnoreCase));
			if (context is not null)
			{
				context.Banner = bannerProvider;
			}
		}
	}

	private void RegisterContext(string template, Delegate? validation, string? description)
	{
		var parsedTemplate = RouteTemplateParser.Parse(template, _options.Parsing);
		RouteConfigurationValidator.ValidateUnique(
			parsedTemplate,
			_contexts.Select(context => context.Template)
		);

		_contexts.Add(new ContextDefinition(parsedTemplate, validation, description));
	}

	private async ValueTask<IReplResult?> ValidateContextsForPathAsync(
		IReadOnlyList<string> matchedPathTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var contextMatches = ContextResolver.ResolvePrefixes(_contexts, matchedPathTokens, _options.Parsing);
		foreach (var contextMatch in contextMatches)
		{
			var validation = await ValidateContextAsync(contextMatch, serviceProvider, cancellationToken).ConfigureAwait(false);
			if (!validation.IsValid)
			{
				return validation.Failure;
			}
		}

		return null;
	}

	private async ValueTask<IReplResult?> ValidateContextsForMatchAsync(
		RouteMatch match,
		IReadOnlyList<string> matchedPathTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var contextMatches = ResolveRouteContextPrefixes(match.Route.Template, matchedPathTokens);
		foreach (var contextMatch in contextMatches)
		{
			var validation = await ValidateContextAsync(contextMatch, serviceProvider, cancellationToken).ConfigureAwait(false);
			if (!validation.IsValid)
			{
				return validation.Failure;
			}
		}

		return null;
	}

	private List<object?> BuildContextHierarchyValues(
		RouteTemplate matchedRouteTemplate,
		IReadOnlyList<string> matchedPathTokens)
	{
		var matches = ResolveRouteContextPrefixes(matchedRouteTemplate, matchedPathTokens);
		var values = new List<object?>();
		foreach (var contextMatch in matches)
		{
			foreach (var dynamicSegment in contextMatch.Context.Template.Segments.OfType<DynamicRouteSegment>())
			{
				if (!contextMatch.RouteValues.TryGetValue(dynamicSegment.Name, out var routeValue))
				{
					continue;
				}

				values.Add(ConvertContextValue(routeValue, dynamicSegment.ConstraintKind));
			}
		}

		return values;
	}

	private IReadOnlyList<ContextMatch> ResolveRouteContextPrefixes(
		RouteTemplate matchedRouteTemplate,
		IReadOnlyList<string> matchedPathTokens)
	{
		var matches = ContextResolver.ResolvePrefixes(_contexts, matchedPathTokens, _options.Parsing);
		return [..
			matches.Where(contextMatch =>
				IsTemplatePrefix(
					contextMatch.Context.Template,
					matchedRouteTemplate)),
		];
	}

	private static bool IsTemplatePrefix(RouteTemplate contextTemplate, RouteTemplate routeTemplate)
	{
		if (contextTemplate.Segments.Count > routeTemplate.Segments.Count)
		{
			return false;
		}

		for (var i = 0; i < contextTemplate.Segments.Count; i++)
		{
			var contextSegment = contextTemplate.Segments[i];
			var routeSegment = routeTemplate.Segments[i];
			if (!AreSegmentsEquivalent(contextSegment, routeSegment))
			{
				return false;
			}
		}

		return true;
	}

	private static bool AreSegmentsEquivalent(RouteSegment left, RouteSegment right)
	{
		if (left is LiteralRouteSegment leftLiteral && right is LiteralRouteSegment rightLiteral)
		{
			return string.Equals(leftLiteral.Value, rightLiteral.Value, StringComparison.OrdinalIgnoreCase);
		}

		if (left is DynamicRouteSegment leftDynamic && right is DynamicRouteSegment rightDynamic)
		{
			if (leftDynamic.ConstraintKind != rightDynamic.ConstraintKind)
			{
				return false;
			}

			if (leftDynamic.ConstraintKind != RouteConstraintKind.Custom)
			{
				return true;
			}

			return string.Equals(
				leftDynamic.CustomConstraintName,
				rightDynamic.CustomConstraintName,
				StringComparison.OrdinalIgnoreCase);
		}

		return false;
	}

	private static object? ConvertContextValue(string routeValue, RouteConstraintKind kind) =>
		kind switch
		{
			RouteConstraintKind.Int => ParameterValueConverter.ConvertSingle(routeValue, typeof(int), CultureInfo.InvariantCulture),
			RouteConstraintKind.Long => ParameterValueConverter.ConvertSingle(routeValue, typeof(long), CultureInfo.InvariantCulture),
			RouteConstraintKind.Bool => ParameterValueConverter.ConvertSingle(routeValue, typeof(bool), CultureInfo.InvariantCulture),
			RouteConstraintKind.Guid => ParameterValueConverter.ConvertSingle(routeValue, typeof(Guid), CultureInfo.InvariantCulture),
			RouteConstraintKind.Uri => ParameterValueConverter.ConvertSingle(routeValue, typeof(Uri), CultureInfo.InvariantCulture),
			RouteConstraintKind.Url => ParameterValueConverter.ConvertSingle(routeValue, typeof(Uri), CultureInfo.InvariantCulture),
			RouteConstraintKind.Urn => ParameterValueConverter.ConvertSingle(routeValue, typeof(Uri), CultureInfo.InvariantCulture),
			RouteConstraintKind.Time => ParameterValueConverter.ConvertSingle(routeValue, typeof(TimeOnly), CultureInfo.InvariantCulture),
			RouteConstraintKind.Date => ParameterValueConverter.ConvertSingle(routeValue, typeof(DateOnly), CultureInfo.InvariantCulture),
			RouteConstraintKind.DateTime => ParameterValueConverter.ConvertSingle(routeValue, typeof(DateTime), CultureInfo.InvariantCulture),
			RouteConstraintKind.DateTimeOffset => ParameterValueConverter.ConvertSingle(routeValue, typeof(DateTimeOffset), CultureInfo.InvariantCulture),
			RouteConstraintKind.TimeSpan => ParameterValueConverter.ConvertSingle(routeValue, typeof(TimeSpan), CultureInfo.InvariantCulture),
			_ => routeValue,
		};

	private async ValueTask<ContextValidationOutcome> ValidateContextAsync(
		ContextMatch contextMatch,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (contextMatch.Context.Validation is null)
		{
			return ContextValidationOutcome.Success;
		}

		var bindingContext = new InvocationBindingContext(
			contextMatch.RouteValues,
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
			[],
			[],
			_options.Parsing.NumericFormatProvider,
			serviceProvider,
			_options.Interaction,
			cancellationToken);
		var arguments = HandlerArgumentBinder.Bind(contextMatch.Context.Validation, bindingContext);
		var validationResult = await CommandInvoker
			.InvokeAsync(contextMatch.Context.Validation, arguments)
			.ConfigureAwait(false);
		return validationResult switch
		{
			bool value => value
				? ContextValidationOutcome.Success
				: ContextValidationOutcome.FromFailure(CreateDefaultContextValidationFailure(contextMatch)),
			IReplResult replResult => string.Equals(replResult.Kind, "text", StringComparison.OrdinalIgnoreCase)
				? ContextValidationOutcome.Success
				: ContextValidationOutcome.FromFailure(replResult),
			string text => string.IsNullOrWhiteSpace(text)
				? ContextValidationOutcome.Success
				: ContextValidationOutcome.FromFailure(Results.Validation(text)),
			null => ContextValidationOutcome.FromFailure(CreateDefaultContextValidationFailure(contextMatch)),
			_ => throw new InvalidOperationException(
				"Context validation must return bool, string, IReplResult, or null."),
		};
	}

	private static IReplResult CreateDefaultContextValidationFailure(ContextMatch contextMatch)
	{
		var scope = contextMatch.Context.Template.Template;
		var details = contextMatch.RouteValues.Count == 0
			? null
			: contextMatch.RouteValues;
		return Results.Validation($"Scope validation failed for '{scope}'.", details);
	}

	private IReplResult CreateUnknownCommandResult(IReadOnlyList<string> tokens)
	{
		var input = string.Join(' ', tokens);
		var visibleRoutes = _routes
			.Where(route => !route.Command.IsHidden)
			.Select(route => route.Template.Template)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var bestSuggestion = FindBestSuggestion(input, visibleRoutes);
		if (bestSuggestion is null)
		{
			return Results.Error("unknown_command", $"Unknown command '{input}'.");
		}

		return Results.Error(
			code: "unknown_command",
			message: $"Unknown command '{input}'. Did you mean '{bestSuggestion}'?");
	}

	private static IReplResult CreateAmbiguousPrefixResult(PrefixResolutionResult prefixResolution)
	{
		var message = $"Ambiguous command prefix '{prefixResolution.AmbiguousToken}'. Candidates: {string.Join(", ", prefixResolution.Candidates)}.";
		return Results.Validation(message);
	}

	private static IReplResult CreateInvalidRouteValueResult(RouteResolver.RouteConstraintFailure failure)
	{
		var expected = GetConstraintDisplayName(failure.Segment);
		var message = $"Invalid value '{failure.Value}' for parameter '{failure.Segment.Name}' (expected: {expected}).";
		return Results.Validation(message);
	}

	private static IReplResult CreateMissingRouteValuesResult(RouteResolver.RouteMissingArgumentsFailure failure)
	{
		if (failure.MissingSegments.Length == 1)
		{
			var segment = failure.MissingSegments[0];
			var expected = GetConstraintDisplayName(segment);
			var message = $"Missing value for parameter '{segment.Name}' (expected: {expected}).";
			return Results.Validation(message);
		}

		var names = string.Join(", ", failure.MissingSegments.Select(segment => segment.Name));
		return Results.Validation($"Missing values for parameters: {names}.");
	}

	private IReplResult CreateRouteResolutionFailureResult(
		IReadOnlyList<string> tokens,
		RouteResolver.RouteConstraintFailure? constraintFailure,
		RouteResolver.RouteMissingArgumentsFailure? missingArgumentsFailure)
	{
		if (constraintFailure is { } routeConstraintFailure)
		{
			return CreateInvalidRouteValueResult(routeConstraintFailure);
		}

		if (missingArgumentsFailure is { } routeMissingArgumentsFailure)
		{
			return CreateMissingRouteValuesResult(routeMissingArgumentsFailure);
		}

		return CreateUnknownCommandResult(tokens);
	}

	private static string GetConstraintDisplayName(DynamicRouteSegment segment) =>
		segment.ConstraintKind == RouteConstraintKind.Custom && !string.IsNullOrWhiteSpace(segment.CustomConstraintName)
			? segment.CustomConstraintName!
			: GetConstraintTypeName(segment.ConstraintKind);

	private async ValueTask TryRenderCommandBannerAsync(
		CommandBuilder command,
		string? outputFormat,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		if (command.Banner is { } banner && ShouldRenderBanner(outputFormat))
		{
			await InvokeBannerAsync(banner, serviceProvider, cancellationToken).ConfigureAwait(false);
		}
	}

	private bool ShouldRenderBanner(string? requestedOutputFormat)
	{
		if (_allBannersSuppressed.Value || !_options.Output.BannerEnabled)
		{
			return false;
		}

		var format = string.IsNullOrWhiteSpace(requestedOutputFormat)
			? _options.Output.DefaultFormat
			: requestedOutputFormat;
		return string.Equals(format, "human", StringComparison.OrdinalIgnoreCase);
	}

	private async ValueTask InvokeBannerAsync(
		Delegate banner,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var bindingContext = new InvocationBindingContext(
			routeValues: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			namedOptions: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
			positionalArguments: [],
			contextValues: [ReplSessionIO.Output],
			numericFormatProvider: _options.Parsing.NumericFormatProvider,
			serviceProvider: serviceProvider,
			interactionOptions: _options.Interaction,
			cancellationToken: cancellationToken);
		var arguments = HandlerArgumentBinder.Bind(banner, bindingContext);
		var result = await CommandInvoker.InvokeAsync(banner, arguments).ConfigureAwait(false);
		if (result is string text && !string.IsNullOrEmpty(text))
		{
			var styled = _options.Output.IsAnsiEnabled()
				? AnsiText.Apply(text, _options.Output.ResolvePalette().BannerStyle)
				: text;
			await ReplSessionIO.Output.WriteLineAsync(styled).ConfigureAwait(false);
		}
	}

	private string BuildBannerText()
	{
		var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
		var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
		var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		var description = _description
			?? assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;

		var header = string.Join(
			' ',
			new[] { product, version }
				.Where(value => !string.IsNullOrWhiteSpace(value))
				.Select(value => value!));

		if (string.IsNullOrWhiteSpace(header))
		{
			return description ?? string.Empty;
		}

		return string.IsNullOrWhiteSpace(description)
			? header
			: $"{header}{Environment.NewLine}{description}";
	}

	private PrefixResolutionResult ResolveUniquePrefixes(IReadOnlyList<string> tokens)
	{
		if (tokens.Count == 0)
		{
			return new PrefixResolutionResult(tokens: []);
		}

		var resolved = tokens.ToArray();
		for (var index = 0; index < resolved.Length; index++)
		{
			// Prefix expansion is only attempted on literal nodes that remain reachable
			// after validating previously resolved segments (including typed dynamics).
			var candidates = ResolveLiteralCandidatesAtIndex(resolved, index);
			if (candidates.Length == 0)
			{
				continue;
			}

			var token = resolved[index];
			var exact = candidates
				.Where(candidate => string.Equals(candidate, token, StringComparison.OrdinalIgnoreCase))
				.ToArray();
			if (exact.Length == 1)
			{
				resolved[index] = exact[0];
				continue;
			}

			var prefixMatches = candidates
				.Where(candidate => candidate.StartsWith(token, StringComparison.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
			if (prefixMatches.Length == 1)
			{
				resolved[index] = prefixMatches[0];
				continue;
			}

			if (prefixMatches.Length > 1)
			{
				// Ambiguous shorthand must fail fast so users don't execute the wrong command.
				return new PrefixResolutionResult(
					tokens: resolved,
					ambiguousToken: token,
					candidates: prefixMatches);
			}
		}

		return new PrefixResolutionResult(tokens: resolved);
	}

	private string[] ResolveLiteralCandidatesAtIndex(string[] tokens, int index)
	{
		var literals = EnumeratePrefixTemplates()
			.Where(template => !template.IsHidden)
			.SelectMany(template => GetCandidateLiterals(template, tokens, index))
			.Where(candidate => !string.IsNullOrWhiteSpace(candidate))
			.Select(candidate => candidate!)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return literals;
	}

	private IEnumerable<PrefixTemplate> EnumeratePrefixTemplates()
	{
		foreach (var route in _routes)
		{
			yield return new PrefixTemplate(route.Template, route.Command.IsHidden, route.Command.Aliases);
		}

		foreach (var context in _contexts)
		{
			yield return new PrefixTemplate(context.Template, IsHidden: false, Aliases: []);
		}
	}

	private IReadOnlyList<string> GetCandidateLiterals(PrefixTemplate template, string[] tokens, int index)
	{
		var routeTemplate = template.Template;
		if (routeTemplate.Segments.Count <= index)
		{
			return [];
		}

		for (var i = 0; i < index; i++)
		{
			var token = tokens[i];
			var segment = routeTemplate.Segments[i];
			// Keep only templates whose resolved prefix still matches the user's input.
			if (segment is LiteralRouteSegment literal
				&& !string.Equals(literal.Value, token, StringComparison.OrdinalIgnoreCase))
			{
				return [];
			}

			if (segment is DynamicRouteSegment dynamic
				&& !RouteConstraintEvaluator.IsMatch(dynamic, token, _options.Parsing))
			{
				return [];
			}
		}

		if (routeTemplate.Segments[index] is not LiteralRouteSegment literalSegment)
		{
			return [];
		}

		if (index == routeTemplate.Segments.Count - 1 && template.Aliases.Count > 0)
		{
			return [literalSegment.Value, .. template.Aliases];
		}

		return [literalSegment.Value];
	}

	private static string? FindBestSuggestion(string input, string[] candidates)
	{
		if (string.IsNullOrWhiteSpace(input) || candidates.Length == 0)
		{
			return null;
		}

		var exactPrefix = candidates
			.FirstOrDefault(candidate =>
				candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrWhiteSpace(exactPrefix))
		{
			return exactPrefix;
		}

		var normalizedInput = input.ToLowerInvariant();
		var minDistance = int.MaxValue;
		string? best = null;
		foreach (var candidate in candidates)
		{
			var distance = ComputeLevenshteinDistance(
				normalizedInput,
				candidate.ToLowerInvariant());
			if (distance < minDistance)
			{
				minDistance = distance;
				best = candidate;
			}
		}

		var threshold = Math.Max(2, normalizedInput.Length / 3);
		return minDistance <= threshold ? best : null;
	}

	internal object CreateDocumentationModel(string? targetPath)
	{
		var normalizedTargetPath = NormalizePath(targetPath);
		var commands = SelectDocumentationCommands(normalizedTargetPath, out var notFoundResult);
		if (notFoundResult is not null)
		{
			return notFoundResult;
		}

		var contexts = SelectDocumentationContexts(normalizedTargetPath, commands);
		var commandDocs = commands.Select(BuildDocumentationCommand).ToArray();
		var contextDocs = contexts
			.Select(context => new ReplDocContext(
				Path: context.Template.Template,
				Description: context.Description,
				IsDynamic: context.Template.Segments.Any(segment => segment is DynamicRouteSegment),
				IsHidden: false))
			.ToArray();
		return new ReplDocumentationModel(
			App: BuildDocumentationApp(),
			Contexts: contextDocs,
			Commands: commandDocs);
	}

	private RouteDefinition[] SelectDocumentationCommands(
		string? normalizedTargetPath,
		out IReplResult? notFoundResult)
	{
		notFoundResult = null;
		if (string.IsNullOrWhiteSpace(normalizedTargetPath))
		{
			return _routes.Where(route => !route.Command.IsHidden).ToArray();
		}

		var exactCommand = _routes.FirstOrDefault(
			route => string.Equals(
				route.Template.Template,
				normalizedTargetPath,
				StringComparison.OrdinalIgnoreCase));
		if (exactCommand is not null)
		{
			return [exactCommand];
		}

		var exactContext = _contexts.FirstOrDefault(
			context => string.Equals(
				context.Template.Template,
				normalizedTargetPath,
				StringComparison.OrdinalIgnoreCase));
		if (exactContext is not null)
		{
			return _routes
				.Where(route =>
					!route.Command.IsHidden
					&& route.Template.Template.StartsWith(
						$"{exactContext.Template.Template} ",
						StringComparison.OrdinalIgnoreCase))
				.ToArray();
		}

		notFoundResult = Results.NotFound($"Documentation target '{normalizedTargetPath}' not found.");
		return [];
	}

	private ContextDefinition[] SelectDocumentationContexts(
		string? normalizedTargetPath,
		RouteDefinition[] commands)
	{
		if (string.IsNullOrWhiteSpace(normalizedTargetPath))
		{
			return [.. _contexts];
		}

		var exactContext = _contexts.FirstOrDefault(
			context => string.Equals(
				context.Template.Template,
				normalizedTargetPath,
				StringComparison.OrdinalIgnoreCase));
		if (exactContext is not null)
		{
			return [exactContext];
		}

		if (commands.Length == 0)
		{
			return [];
		}

		var selected = _contexts
			.Where(context => commands.Any(command =>
				command.Template.Template.StartsWith(
					$"{context.Template.Template} ",
					StringComparison.OrdinalIgnoreCase)
				|| string.Equals(
					command.Template.Template,
					context.Template.Template,
					StringComparison.OrdinalIgnoreCase)))
			.ToArray();
		return selected;
	}

	private ReplDocCommand BuildDocumentationCommand(RouteDefinition route)
	{
		var dynamicSegments = route.Template.Segments
			.OfType<DynamicRouteSegment>()
			.ToArray();
		var routeParameterNames = dynamicSegments
			.Select(segment => segment.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var arguments = dynamicSegments
			.Select(segment => new ReplDocArgument(
				Name: segment.Name,
				Type: GetConstraintTypeName(segment.ConstraintKind),
				Required: !segment.IsOptional,
				Description: null))
			.ToArray();

		var options = route.Command.Handler.Method
			.GetParameters()
			.Where(parameter =>
				!string.IsNullOrWhiteSpace(parameter.Name)
				&& parameter.ParameterType != typeof(CancellationToken)
				&& !routeParameterNames.Contains(parameter.Name!)
				&& !IsFrameworkInjectedParameter(parameter.ParameterType))
			.Select(parameter => new ReplDocOption(
				Name: parameter.Name!,
				Type: GetFriendlyTypeName(parameter.ParameterType),
				Required: IsRequiredParameter(parameter),
				Description: parameter.GetCustomAttribute<DescriptionAttribute>()?.Description))
			.ToArray();

		return new ReplDocCommand(
			Path: route.Template.Template,
			Description: route.Command.Description,
			Aliases: route.Command.Aliases,
			IsHidden: route.Command.IsHidden,
			Arguments: arguments,
			Options: options);
	}

	private ReplDocApp BuildDocumentationApp()
	{
		var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
		var name = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
			?? assembly.GetName().Name
			?? "repl";
		var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? assembly.GetName().Version?.ToString();
		var description = _description
			?? assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;
		return new ReplDocApp(name, version, description);
	}

	private static bool IsFrameworkInjectedParameter(Type parameterType) =>
		parameterType == typeof(IServiceProvider)
		|| parameterType == typeof(IReplSessionState)
		|| parameterType == typeof(IReplInteractionChannel)
		|| parameterType == typeof(IReplKeyReader);

	private static bool IsRequiredParameter(ParameterInfo parameter)
	{
		if (parameter.HasDefaultValue)
		{
			return false;
		}

		if (!parameter.ParameterType.IsValueType)
		{
			return false;
		}

		return Nullable.GetUnderlyingType(parameter.ParameterType) is null;
	}

	private static string GetConstraintTypeName(RouteConstraintKind kind) =>
		kind switch
		{
			RouteConstraintKind.String => "string",
			RouteConstraintKind.Alpha => "string",
			RouteConstraintKind.Bool => "bool",
			RouteConstraintKind.Email => "email",
			RouteConstraintKind.Uri => "uri",
			RouteConstraintKind.Url => "url",
			RouteConstraintKind.Urn => "urn",
			RouteConstraintKind.Time => "time",
			RouteConstraintKind.Date => "date",
			RouteConstraintKind.DateTime => "datetime",
			RouteConstraintKind.DateTimeOffset => "datetimeoffset",
			RouteConstraintKind.TimeSpan => "timespan",
			RouteConstraintKind.Guid => "guid",
			RouteConstraintKind.Long => "long",
			RouteConstraintKind.Int => "int",
			RouteConstraintKind.Custom => "custom",
			_ => "string",
		};

	private static string GetFriendlyTypeName(Type type)
	{
		var underlying = Nullable.GetUnderlyingType(type);
		if (underlying is not null)
		{
			return $"{GetFriendlyTypeName(underlying)}?";
		}

		if (!type.IsGenericType)
		{
			return type.Name.ToLowerInvariant() switch
			{
				"string" => "string",
				"int32" => "int",
				"int64" => "long",
				"boolean" => "bool",
				"double" => "double",
				"decimal" => "decimal",
				"dateonly" => "date",
				"datetime" => "datetime",
				"timeonly" => "time",
				"datetimeoffset" => "datetimeoffset",
				"timespan" => "timespan",
				_ => type.Name,
			};
		}

		var genericName = type.Name[..type.Name.IndexOf('`')];
		var genericArgs = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
		return $"{genericName}<{genericArgs}>";
	}

	private static string? NormalizePath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		var parts = path
			.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return parts.Length == 0
			? null
			: string.Join(' ', parts);
	}

	private static int ComputeLevenshteinDistance(string source, string target)
	{
		var rows = source.Length + 1;
		var cols = target.Length + 1;
		var matrix = new int[rows, cols];

		for (var i = 0; i < rows; i++)
		{
			matrix[i, 0] = i;
		}

		for (var j = 0; j < cols; j++)
		{
			matrix[0, j] = j;
		}

		for (var i = 1; i < rows; i++)
		{
			for (var j = 1; j < cols; j++)
			{
				var cost = source[i - 1] == target[j - 1] ? 0 : 1;
				matrix[i, j] = Math.Min(
					Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
					matrix[i - 1, j - 1] + cost);
			}
		}

		return matrix[rows - 1, cols - 1];
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

	private static void ApplyMetadataFromAttributes(CommandBuilder command, Delegate handler)
	{
		ArgumentNullException.ThrowIfNull(command);
		ArgumentNullException.ThrowIfNull(handler);

		var description = handler.Method.GetCustomAttribute<DescriptionAttribute>();
		if (description is not null && !string.IsNullOrWhiteSpace(description.Description))
		{
			command.WithDescription(description.Description);
		}

		var browsable = handler.Method.GetCustomAttribute<BrowsableAttribute>();
		if (browsable is not null && !browsable.Browsable)
		{
			command.Hidden();
		}
	}

	private DefaultServiceProvider CreateDefaultServiceProvider()
	{
		var defaults = new Dictionary<Type, object>
		{
			[typeof(IReplSessionState)] = new InMemoryReplSessionState(),
			[typeof(IReplInteractionChannel)] = new ConsoleInteractionChannel(_options.Interaction, _options.Output),
			[typeof(IHistoryProvider)] = _options.Interactive.HistoryProvider ?? new InMemoryHistoryProvider(),
			[typeof(IReplKeyReader)] = new ConsoleKeyReader(),
			[typeof(IReplSessionInfo)] = new LiveSessionInfo(),
			[typeof(TimeProvider)] = TimeProvider.System,
		};
		return new DefaultServiceProvider(defaults);
	}

	private static bool IsHelpToken(string token) =>
		string.Equals(token, "help", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(token, "?", StringComparison.Ordinal);

	private string BuildHumanHelp(IReadOnlyList<string> scopeTokens)
	{
		var settings = _options.Output.ResolveHumanRenderSettings();
		return HelpTextBuilder.Build(
			_routes,
			_contexts,
			scopeTokens,
			_options.Parsing,
			_options.AmbientCommands,
			renderWidth: settings.Width,
			useAnsi: settings.UseAnsi,
			palette: settings.Palette);
	}

	private readonly record struct ContextValidationOutcome(bool IsValid, IReplResult? Failure)
	{
		public static ContextValidationOutcome Success { get; } =
			new(IsValid: true, Failure: null);

		public static ContextValidationOutcome FromFailure(IReplResult failure) =>
			new(IsValid: false, Failure: failure);
	}

	private readonly record struct PrefixTemplate(
		RouteTemplate Template,
		bool IsHidden,
		IReadOnlyList<string> Aliases);

	private enum AmbientCommandOutcome
	{
		NotHandled,
		Handled,
		HandledError,
		Exit,
	}

	private sealed class DefaultServiceProvider(IReadOnlyDictionary<Type, object> services) : IServiceProvider
	{
		public object? GetService(Type serviceType)
		{
			ArgumentNullException.ThrowIfNull(serviceType);
			return services.TryGetValue(serviceType, out var service) ? service : null;
		}
	}

	private InvocationBindingContext CreateInvocationBindingContext(
		RouteMatch match,
		OptionParsingResult parsedOptions,
		string[] matchedPathTokens,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var contextValues = BuildContextHierarchyValues(match.Route.Template, matchedPathTokens);
		return new InvocationBindingContext(
			match.Values,
			parsedOptions.NamedOptions,
			parsedOptions.PositionalArguments,
			contextValues,
			_options.Parsing.NumericFormatProvider,
			serviceProvider,
			_options.Interaction,
			cancellationToken);
	}
}
