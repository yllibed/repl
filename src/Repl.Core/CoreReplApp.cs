using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Repl.ShellCompletion;

namespace Repl;

/// <summary>
/// Entry point for configuring and running a REPL application.
/// </summary>
public sealed partial class CoreReplApp : ICoreReplApp
{
	private const string AutocompleteModeSessionStateKey = "__repl.autocomplete.mode";

	private readonly List<CommandBuilder> _commands = [];
	private readonly List<ContextDefinition> _contexts = [];
	private readonly List<RouteDefinition> _routes = [];
	private readonly List<Func<ReplExecutionContext, ReplNext, ValueTask>> _middleware = [];
	private readonly ReplOptions _options = new();
	private readonly List<ModuleRegistration> _moduleRegistrations = [];
	private readonly Stack<int> _moduleMappingScope = new();
	private int _nextModuleId = 1;
	private readonly AsyncLocal<InvocationRuntimeState?> _runtimeState = new();
	private readonly ConditionalWeakTable<IServiceProvider, RoutingCacheBucket> _routingCacheByServiceProvider = new();
	private long _routingCacheVersion;
	private readonly DefaultServiceProvider _services;
	private readonly ShellCompletionRuntime _shellCompletionRuntime;
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
		_shellCompletionRuntime = new ShellCompletionRuntime(
			_options,
			ResolveEntryAssemblyName,
			ResolveShellCompletionCommandName,
			ResolveShellCompletionCandidates);
		_moduleRegistrations.Add(new ModuleRegistration(ModuleId: 0, IsPresent: static _ => true));
		_moduleMappingScope.Push(0);
		MapModule(
			new ShellCompletionModule(_shellCompletionRuntime),
			static context => context.Channel is ReplRuntimeChannel.Cli or ReplRuntimeChannel.Interactive);
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
	/// Invalidates active routing cache so module presence predicates are re-evaluated on next resolution.
	/// </summary>
	public void InvalidateRouting() => Interlocked.Increment(ref _routingCacheVersion);

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
		var moduleId = ResolveCurrentMappingModuleId();
		RouteConfigurationValidator.ValidateUnique(
			template,
			_routes
				.Where(existingRoute => existingRoute.ModuleId == moduleId)
				.Select(existingRoute => existingRoute.Template));

		_commands.Add(command);
		var routeDefinition = new RouteDefinition(template, command, moduleId);
		_routes.Add(routeDefinition);
		InvalidateRouting();
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
	/// <returns>A context builder for context-level metadata configuration.</returns>
	public IContextBuilder Context(string segment, Action<ICoreReplApp> configure, Delegate? validation = null)
	{
		segment = string.IsNullOrWhiteSpace(segment)
			? throw new ArgumentException("Segment cannot be empty.", nameof(segment))
			: segment;
		ArgumentNullException.ThrowIfNull(configure);
		var contextDescription = configure.Method.GetCustomAttribute<DescriptionAttribute>()?.Description;
		var context = RegisterContext(segment, validation, contextDescription);

		var scopedMap = new ScopedMap(this, segment, context);
		configure(scopedMap);
		return new ContextBuilder(this, context);
	}

	/// <summary>
	/// Creates a top-level context segment and configures nested routes.
	/// Compatibility overload for <see cref="IReplMap"/> callbacks.
	/// </summary>
	public IContextBuilder Context(string segment, Action<IReplMap> configure, Delegate? validation = null)
	{
		segment = string.IsNullOrWhiteSpace(segment)
			? throw new ArgumentException("Segment cannot be empty.", nameof(segment))
			: segment;
		ArgumentNullException.ThrowIfNull(configure);
		var contextDescription = configure.Method.GetCustomAttribute<DescriptionAttribute>()?.Description;
		var context = RegisterContext(segment, validation, contextDescription);

		var scopedMap = new ScopedMap(this, segment, context);
		configure(scopedMap);
		return new ContextBuilder(this, context);
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
	/// Maps a module resolved through runtime activation with a runtime presence predicate.
	/// </summary>
	/// <typeparam name="TModule">Module type.</typeparam>
	/// <param name="isPresent">Runtime presence predicate.</param>
	/// <returns>The same app instance.</returns>
	public CoreReplApp MapModule<
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
		TModule>(Func<ModulePresenceContext, bool> isPresent)
		where TModule : class, IReplModule
	{
		ArgumentNullException.ThrowIfNull(isPresent);
		var module = _services.GetService(typeof(TModule)) as TModule
			?? Activator.CreateInstance(typeof(TModule)) as TModule
			?? throw new InvalidOperationException(
				$"Unable to activate module '{typeof(TModule).FullName}'. Provide a parameterless constructor or map an instance.");
		return MapModule(module, isPresent);
	}

	/// <summary>
	/// Maps a module instance.
	/// </summary>
	/// <param name="module">Module instance.</param>
	/// <returns>The same app instance.</returns>
	public CoreReplApp MapModule(IReplModule module)
	{
		ArgumentNullException.ThrowIfNull(module);
		MapModuleCore(module, static _ => true, this);
		return this;
	}

	/// <summary>
	/// Maps a module instance with a runtime presence predicate.
	/// </summary>
	/// <param name="module">Module instance.</param>
	/// <param name="isPresent">Runtime presence predicate.</param>
	/// <returns>The same app instance.</returns>
	public CoreReplApp MapModule(
		IReplModule module,
		Func<ModulePresenceContext, bool> isPresent)
	{
		ArgumentNullException.ThrowIfNull(module);
		ArgumentNullException.ThrowIfNull(isPresent);
		MapModuleCore(module, isPresent, this);
		return this;
	}

	IContextBuilder ICoreReplApp.Context(string segment, Action<ICoreReplApp> configure, Delegate? validation) =>
		Context(segment, configure, validation);

	ICoreReplApp ICoreReplApp.MapModule(IReplModule module) => MapModule(module);

	ICoreReplApp ICoreReplApp.MapModule(IReplModule module, Func<ModulePresenceContext, bool> isPresent) =>
		MapModule(module, isPresent);

	ICoreReplApp ICoreReplApp.WithBanner(Delegate bannerProvider) => WithBanner(bannerProvider);

	ICoreReplApp ICoreReplApp.WithBanner(string text) => WithBanner(text);

	void ICoreReplApp.InvalidateRouting() => InvalidateRouting();

	IContextBuilder IReplMap.Context(string segment, Action<IReplMap> configure, Delegate? validation) =>
		Context(segment, configure, validation);

	IReplMap IReplMap.MapModule(IReplModule module) => MapModule(module);

	IReplMap IReplMap.MapModule(IReplModule module, Func<ModulePresenceContext, bool> isPresent) =>
		MapModule(module, isPresent);

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

	internal RouteMatch? Resolve(IReadOnlyList<string> inputTokens)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		return Resolve(inputTokens, activeGraph.Routes);
	}

	internal RouteResolver.RouteResolutionResult ResolveWithDiagnostics(IReadOnlyList<string> inputTokens)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		return ResolveWithDiagnostics(inputTokens, activeGraph.Routes);
	}

	private RouteMatch? Resolve(IReadOnlyList<string> inputTokens, IReadOnlyList<RouteDefinition> routes) =>
		RouteResolver.Resolve(routes, inputTokens, _options.Parsing);

	private RouteResolver.RouteResolutionResult ResolveWithDiagnostics(
		IReadOnlyList<string> inputTokens,
		IReadOnlyList<RouteDefinition> routes) =>
		RouteResolver.ResolveWithDiagnostics(routes, inputTokens, _options.Parsing);

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
			using var runtimeStateScope = PushRuntimeState(serviceProvider, isInteractiveSession: false);
			if (!_shellCompletionRuntime.IsBridgeInvocation(globalOptions.RemainingTokens))
			{
				await TryRenderBannerAsync(globalOptions, serviceProvider, cancellationToken).ConfigureAwait(false);
			}
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

	private void MapModuleCore(
		IReplModule module,
		Func<ModulePresenceContext, bool> isPresent,
		IReplMap map)
	{
		var moduleId = RegisterModule(isPresent);
		_moduleMappingScope.Push(moduleId);
		try
		{
			module.Map(map);
		}
		finally
		{
			_ = _moduleMappingScope.Pop();
		}
	}

	private int RegisterModule(Func<ModulePresenceContext, bool> isPresent)
	{
		var moduleId = _nextModuleId++;
		_moduleRegistrations.Add(new ModuleRegistration(moduleId, isPresent));
		InvalidateRouting();
		return moduleId;
	}

	private int ResolveCurrentMappingModuleId() =>
		_moduleMappingScope.Count == 0 ? 0 : _moduleMappingScope.Peek();

	private ReplRuntimeChannel ResolveCurrentRuntimeChannel()
	{
		if (ReplSessionIO.IsSessionActive)
		{
			return ReplRuntimeChannel.Session;
		}

		return _runtimeState.Value?.IsInteractiveSession == true
			? ReplRuntimeChannel.Interactive
			: ReplRuntimeChannel.Cli;
	}

	private ActiveRoutingGraph ResolveActiveRoutingGraph()
	{
		var runtime = _runtimeState.Value;
		var serviceProvider = runtime?.ServiceProvider ?? _services;
		var channel = ResolveCurrentRuntimeChannel();
		var cacheVersion = Interlocked.Read(ref _routingCacheVersion);
		var cacheBucket = _routingCacheByServiceProvider.GetOrCreateValue(serviceProvider);
		if (cacheBucket.TryGet(channel, cacheVersion, out var cached))
		{
			return cached;
		}

		var presenceContext = CreateModulePresenceContext(serviceProvider, channel);
		var activeModuleIds = ResolveActiveModuleIds(presenceContext);
		var routes = ResolveActiveRoutes(activeModuleIds);
		var contexts = ResolveActiveContexts(activeModuleIds);
		var computed = new ActiveRoutingGraph(routes, contexts, channel);
		cacheBucket.Set(channel, cacheVersion, computed);
		return computed;
	}

	private static ModulePresenceContext CreateModulePresenceContext(
		IServiceProvider serviceProvider,
		ReplRuntimeChannel channel)
	{
		var sessionState = serviceProvider.GetService(typeof(IReplSessionState)) as IReplSessionState
			?? new InMemoryReplSessionState();
		var sessionInfo = serviceProvider.GetService(typeof(IReplSessionInfo)) as IReplSessionInfo
			?? new LiveSessionInfo();
		return new ModulePresenceContext(serviceProvider, channel, sessionState, sessionInfo);
	}

	private HashSet<int> ResolveActiveModuleIds(ModulePresenceContext context)
	{
		var active = new HashSet<int>();
		foreach (var module in _moduleRegistrations)
		{
			var isActive = false;
			try
			{
				isActive = module.IsPresent(context);
			}
			catch
			{
				isActive = false;
			}

			if (isActive)
			{
				active.Add(module.ModuleId);
			}
		}

		active.Add(0);
		return active;
	}

	private RouteDefinition[] ResolveActiveRoutes(HashSet<int> activeModuleIds)
	{
		var routesByPath = new Dictionary<string, (RouteDefinition Route, int Index)>(StringComparer.OrdinalIgnoreCase);
		for (var index = 0; index < _routes.Count; index++)
		{
			var route = _routes[index];
			if (!activeModuleIds.Contains(route.ModuleId))
			{
				continue;
			}

			routesByPath[route.Template.Template] = (route, index);
		}

		return [..
			routesByPath.Values
				.OrderBy(static item => item.Index)
				.Select(static item => item.Route),
		];
	}

	private ContextDefinition[] ResolveActiveContexts(HashSet<int> activeModuleIds)
	{
		var contextsByPath = new Dictionary<string, (ContextDefinition Context, int Index)>(StringComparer.OrdinalIgnoreCase);
		for (var index = 0; index < _contexts.Count; index++)
		{
			var context = _contexts[index];
			if (!activeModuleIds.Contains(context.ModuleId))
			{
				continue;
			}

			contextsByPath[context.Template.Template] = (context, index);
		}

		return [..
			contextsByPath.Values
				.OrderBy(static item => item.Index)
				.Select(static item => item.Context),
		];
	}

	private RuntimeStateScope PushRuntimeState(IServiceProvider serviceProvider, bool isInteractiveSession)
	{
		var previous = _runtimeState.Value;
		_runtimeState.Value = new InvocationRuntimeState(serviceProvider, isInteractiveSession);
		return new RuntimeStateScope(_runtimeState, previous);
	}

	private static string ResolveEntryAssemblyName()
	{
		var entryAssembly = Assembly.GetEntryAssembly();
		return entryAssembly?.GetName().Name
			?? Assembly.GetExecutingAssembly().GetName().Name
			?? string.Empty;
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
	private async ValueTask<int> ExecuteMatchedCommandAsync(
		RouteMatch match,
		GlobalInvocationOptions globalOptions,
		IServiceProvider serviceProvider,
		List<string>? scopeTokens,
		CancellationToken cancellationToken)
	{
		var activeGraph = ResolveActiveRoutingGraph();
		_options.Interaction.SetPrefilledAnswers(globalOptions.PromptAnswers);
		var parsedOptions = InvocationOptionParser.Parse(match.RemainingTokens);
		var matchedPathLength = globalOptions.RemainingTokens.Count - match.RemainingTokens.Count;
		var matchedPathTokens = globalOptions.RemainingTokens.Take(matchedPathLength).ToArray();
		var bindingContext = CreateInvocationBindingContext(
			match,
			parsedOptions,
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

	private async ValueTask<bool> RenderOutputAsync(
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

	private async ValueTask<bool> RenderHelpAsync(
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
			[typeof(CoreReplApp)] = this,
			[typeof(ICoreReplApp)] = this,
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
		var activeGraph = ResolveActiveRoutingGraph();
		var discoverableRoutes = ResolveDiscoverableRoutes(
			activeGraph.Routes,
			activeGraph.Contexts,
			scopeTokens,
			StringComparison.OrdinalIgnoreCase);
		var discoverableContexts = ResolveDiscoverableContexts(
			activeGraph.Contexts,
			scopeTokens,
			StringComparison.OrdinalIgnoreCase);
		var settings = _options.Output.ResolveHumanRenderSettings();
		return HelpTextBuilder.Build(
			discoverableRoutes,
			discoverableContexts,
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

	private readonly record struct ActiveRoutingGraph(
		RouteDefinition[] Routes,
		ContextDefinition[] Contexts,
		ReplRuntimeChannel Channel);

	private readonly record struct ModuleRegistration(
		int ModuleId,
		Func<ModulePresenceContext, bool> IsPresent);

	private readonly record struct InvocationRuntimeState(
		IServiceProvider ServiceProvider,
		bool IsInteractiveSession);

	private sealed class RoutingCacheEntry(long version, ActiveRoutingGraph graph)
	{
		public long Version { get; } = version;

		public ActiveRoutingGraph Graph { get; } = graph;
	}

	private enum AmbientCommandOutcome
	{
		NotHandled,
		Handled,
		HandledError,
		Exit,
	}

	private sealed class RuntimeStateScope(
		AsyncLocal<InvocationRuntimeState?> state,
		InvocationRuntimeState? previous) : IDisposable
	{
		private readonly AsyncLocal<InvocationRuntimeState?> _state = state;
		private readonly InvocationRuntimeState? _previous = previous;
		private bool _disposed;

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_state.Value = _previous;
			_disposed = true;
		}
	}

	private sealed class RoutingCacheBucket
	{
		private readonly System.Threading.Lock _syncRoot = new();
		private readonly Dictionary<ReplRuntimeChannel, RoutingCacheEntry> _entries = [];

		public bool TryGet(ReplRuntimeChannel channel, long version, out ActiveRoutingGraph graph)
		{
			lock (_syncRoot)
			{
				if (_entries.TryGetValue(channel, out var cached) && cached.Version == version)
				{
					graph = cached.Graph;
					return true;
				}
			}

			graph = default;
			return false;
		}

		public void Set(ReplRuntimeChannel channel, long version, ActiveRoutingGraph graph)
		{
			lock (_syncRoot)
			{
				_entries[channel] = new RoutingCacheEntry(version, graph);
			}
		}
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
		IReadOnlyList<ContextDefinition> contexts,
		IServiceProvider serviceProvider,
		CancellationToken cancellationToken)
	{
		var contextValues = BuildContextHierarchyValues(match.Route.Template, matchedPathTokens, contexts);
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
