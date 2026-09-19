using System.Buffers;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Repl.ShellCompletion;
using Repl.Internal.Options;

namespace Repl;

/// <summary>
/// Entry point for configuring and running a REPL application.
/// </summary>
public sealed partial class CoreReplApp : ICoreReplApp
{
	private readonly List<CommandBuilder> _commands = [];
	private readonly List<ContextDefinition> _contexts = [];
	private readonly List<RouteDefinition> _routes = [];
	private readonly List<Func<ReplExecutionContext, ReplNext, ValueTask>> _middleware = [];
	private readonly ReplOptions _options = new();
	private readonly ImplicitServiceParameterRegistry _implicitServiceParameters = new();
	private readonly List<ModuleRegistration> _moduleRegistrations = [];
	private readonly Stack<int> _moduleMappingScope = new();
	private int _nextModuleId = 1;
	private readonly AsyncLocal<InvocationRuntimeState?> _runtimeState = new();
	private readonly ConditionalWeakTable<IServiceProvider, RoutingCacheBucket> _routingCacheByServiceProvider = new();
	private readonly object _configurationMutationGate = new();
	private long _routingCacheVersion;
	private readonly AsyncLocal<OptionsConfigurationScope?> _optionsConfigurationScope = new();
	private readonly DefaultServiceProvider _services;
	private readonly ShellCompletionRuntime _shellCompletionRuntime;
	private string? _description;
	private Delegate? _banner;
	private readonly AsyncLocal<bool> _bannerRendered = new();
	private readonly AsyncLocal<bool> _allBannersSuppressed = new();
	private readonly GlobalOptionsSnapshot _globalOptionsSnapshot;

	internal ReplOptions OptionsSnapshot => _options;
	internal string? Description => _description;
	internal IGlobalOptionsAccessor GlobalOptionsAccessor => _globalOptionsSnapshot;
	internal GlobalOptionsSnapshot GlobalOptionsSnapshotInstance => _globalOptionsSnapshot;
	internal ImplicitServiceParameterRegistry ImplicitServiceParameters => _implicitServiceParameters;
	internal ShellCompletionRuntime ShellCompletionRuntimeInstance => _shellCompletionRuntime;
	internal IServiceProvider CurrentServiceProvider => _runtimeState.Value?.ServiceProvider ?? _services;
	internal IReplExecutionObserver? ExecutionObserver { get; set; }
	internal List<ContextDefinition> Contexts => _contexts;
	internal AsyncLocal<bool> BannerRendered => _bannerRendered;
	internal AsyncLocal<bool> AllBannersSuppressed => _allBannersSuppressed;
	internal Delegate? Banner => _banner;

	private CoreReplApp()
	{
		_options.Output.SetHostAnsiSupportResolver(() => _options.Capabilities.SupportsAnsi);
		_options.Parsing.ConfigureGlobalDiscoveryMutationCallbacks(
			ValidateMappedOptionCaseSensitivity,
			InvalidateRoutingFromOptionsMutation,
			_configurationMutationGate);
		_globalOptionsSnapshot = new GlobalOptionsSnapshot(_options.Parsing);
		_services = CreateDefaultServiceProvider();
		_shellCompletionRuntime = new ShellCompletionRuntime(
			_options,
			ResolveEntryAssemblyName,
			ResolveShellCompletionCommandName,
			ResolveShellCompletionCandidatesAsync);
		_moduleRegistrations.Add(new ModuleRegistration(ModuleId: 0, IsPresent: static _ => true));
		_moduleMappingScope.Push(0);
		MapModule(
			new ShellCompletionModule(_shellCompletionRuntime),
			static context => context.Channel is ReplRuntimeChannel.Cli or ReplRuntimeChannel.Interactive);
	}

	internal void RegisterGlobalOptionsType(Type optionsType)
	{
		ArgumentNullException.ThrowIfNull(optionsType);
		_implicitServiceParameters.AddGlobalOptionsType(optionsType);
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
		var parentScope = _optionsConfigurationScope.Value;
		var scope = new OptionsConfigurationScope();
		_optionsConfigurationScope.Value = scope;
		ExceptionDispatchInfo? configurationFailure = null;
		IReadOnlyList<Exception> routingInvalidationExceptions = [];
		var mergedIntoParent = true;
		try
		{
			configure(_options);
		}
		catch (Exception exception)
		{
			configurationFailure = ExceptionDispatchInfo.Capture(exception);
		}
		finally
		{
			routingInvalidationExceptions = scope.CloseAndDrain();
			_optionsConfigurationScope.Value = parentScope;
			if (parentScope is not null)
			{
				mergedIntoParent = parentScope.TryAddRange(routingInvalidationExceptions);
			}
		}

		if (configurationFailure is not null)
		{
			configurationFailure.Throw();
		}

		if (parentScope is null || !mergedIntoParent)
		{
			ThrowRoutingInvalidationExceptions(routingInvalidationExceptions);
		}

		return this;
	}

	private void ValidateMappedOptionCaseSensitivity(ReplCaseSensitivity caseSensitivity)
	{
		foreach (var route in _routes)
		{
			OptionSchemaBuilder.ValidateTokenCollisions(
				route.Command.OptionSchema.Entries,
				caseSensitivity,
				route.Template);
		}
	}

	/// <summary>
	/// Occurs after routing has been invalidated.
	/// Kept with its original delegate type for separately packaged binary consumers.
	/// </summary>
	internal event EventHandler? RoutingInvalidated;

	/// <summary>
	/// Occurs after routing has been invalidated and includes discovery-retraction details.
	/// </summary>
	internal event EventHandler<RoutingInvalidatedEventArgs>? RoutingInvalidatedDetailed;

	/// <summary>
	/// Invalidates active routing cache so module presence predicates are re-evaluated on next resolution.
	/// </summary>
	public void InvalidateRouting() => InvalidateRouting(isVisibilityRetraction: false);

	private void InvalidateRouting(bool isVisibilityRetraction) =>
		ThrowRoutingInvalidationExceptions(NotifyRoutingInvalidated(isVisibilityRetraction));

	private void InvalidateRoutingFromOptionsMutation()
	{
		var exceptions = NotifyRoutingInvalidated(isVisibilityRetraction: true);
		if (exceptions is null)
		{
			return;
		}

		if (_optionsConfigurationScope.Value is { } scope && scope.TryAddRange(exceptions))
		{
			return;
		}

		ThrowRoutingInvalidationExceptions(exceptions);
	}

	private List<Exception>? NotifyRoutingInvalidated(bool isVisibilityRetraction)
	{
		Interlocked.Increment(ref _routingCacheVersion);
		List<Exception>? exceptions = null;
		foreach (EventHandler handler in RoutingInvalidated?.GetInvocationList() ?? [])
		{
			try
			{
				handler(this, EventArgs.Empty);
			}
			catch (Exception exception)
			{
				(exceptions ??= []).Add(exception);
			}
		}

		var eventArgs = new RoutingInvalidatedEventArgs(isVisibilityRetraction);
		foreach (EventHandler<RoutingInvalidatedEventArgs> handler in RoutingInvalidatedDetailed?.GetInvocationList() ?? [])
		{
			try
			{
				handler(this, eventArgs);
			}
			catch (Exception exception)
			{
				(exceptions ??= []).Add(exception);
			}
		}

		return exceptions;
	}

	private static void ThrowRoutingInvalidationExceptions(IReadOnlyList<Exception>? exceptions)
	{
		if (exceptions is [var singleException])
		{
			ExceptionDispatchInfo.Capture(singleException).Throw();
		}

		if (exceptions is { Count: > 1 })
		{
			throw new AggregateException("Multiple routing-invalidation subscribers failed.", exceptions);
		}
	}

	private sealed class OptionsConfigurationScope
	{
		private readonly Lock _gate = new();
		private readonly List<Exception> _routingInvalidationExceptions = [];
		private bool _closed;

		public bool TryAddRange(IEnumerable<Exception> exceptions)
		{
			lock (_gate)
			{
				if (_closed)
				{
					return false;
				}

				_routingInvalidationExceptions.AddRange(exceptions);
				return true;
			}
		}

		public IReadOnlyList<Exception> CloseAndDrain()
		{
			lock (_gate)
			{
				_closed = true;
				return [.. _routingInvalidationExceptions];
			}
		}
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

		CommandBuilder command;
		lock (_configurationMutationGate)
		{
			command = new CommandBuilder(
				route,
				handler,
				InvalidateRouting,
				() => _options.Parsing.OptionCaseSensitivity);
			ApplyMetadataFromAttributes(command, handler);
			var parsedTemplate = RouteTemplateParser.Parse(route, _options.Parsing);
			var template = InferRouteConstraintsFromHandler(parsedTemplate, handler);
			var moduleId = ResolveCurrentMappingModuleId();
			RouteConfigurationValidator.ValidateUnique(
				template,
				_routes
					.Where(existingRoute => existingRoute.ModuleId == moduleId)
					.Select(existingRoute => existingRoute.Template));

			var optionSchema = OptionSchemaBuilder.Build(
				template,
				command,
				_options.Parsing,
				_implicitServiceParameters,
				() => _options.Parsing.OptionCaseSensitivity);
			command.AttachOptionSchema(optionSchema);
			command.ValidateOptionVisibility();
			var routeDefinition = new RouteDefinition(template, command, moduleId);
			_commands.Add(command);
			_routes.Add(routeDefinition);
		}

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

	internal RouteMatch? Resolve(IReadOnlyList<string> inputTokens, IReadOnlyList<RouteDefinition> routes) =>
		RouteResolver.Resolve(routes, inputTokens, _options.Parsing);

	internal RouteResolver.RouteResolutionResult ResolveWithDiagnostics(
		IReadOnlyList<string> inputTokens,
		IReadOnlyList<RouteDefinition> routes) =>
		RouteResolver.ResolveWithDiagnostics(routes, inputTokens, _options.Parsing);

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

	internal int ResolveCurrentMappingModuleId() =>
		_moduleMappingScope.Count == 0 ? 0 : _moduleMappingScope.Peek();

	private ReplRuntimeChannel ResolveCurrentRuntimeChannel()
	{
		if (ReplSessionIO.IsProgrammatic)
		{
			return ReplRuntimeChannel.Programmatic;
		}

		if (ReplSessionIO.IsHostedSession)
		{
			return ReplRuntimeChannel.Session;
		}

		return _runtimeState.Value?.IsInteractiveSession == true
			? ReplRuntimeChannel.Interactive
			: ReplRuntimeChannel.Cli;
	}

	internal ActiveRoutingGraph ResolveActiveRoutingGraph() => ResolveActiveRoutingGraph(useDurableCache: true);

	// Module presence can depend on mutable global state (a global option like --env gating a
	// module) that is NOT part of the cache key. Execution applies the invocation's globals
	// before resolving, so its graph is authoritative and may be cached. Completion evaluates
	// the graph at a different moment (the line's globals are not applied), so it must NOT
	// write its provisional graph into the durable cache — otherwise a committed, valid command
	// could later read a completion-time module-absent graph and fail as "Unknown command".
	// Completion passes useDurableCache: false: it neither reads nor writes the shared cache,
	// computing a fresh graph scoped to this pass.
	internal ActiveRoutingGraph ResolveActiveRoutingGraph(bool useDurableCache)
	{
		var runtime = _runtimeState.Value;
		// Presence decides the graph, so it is what the cache is keyed on and what the predicates see.
		var serviceProvider = runtime?.PresenceServiceProvider ?? runtime?.ServiceProvider ?? _services;
		var channel = ResolveCurrentRuntimeChannel();
		var cacheVersion = Interlocked.Read(ref _routingCacheVersion);
		var cacheBucket = _routingCacheByServiceProvider.GetOrCreateValue(serviceProvider);
		if (useDurableCache && cacheBucket.TryGet(channel, cacheVersion, out var cached))
		{
			return cached;
		}

		var presenceContext = CreateModulePresenceContext(serviceProvider, channel);
		var activeModuleIds = ResolveActiveModuleIds(presenceContext);
		var routes = ResolveActiveRoutes(activeModuleIds);
		var contexts = ResolveActiveContexts(activeModuleIds);
		var computed = new ActiveRoutingGraph(routes, contexts, channel);
		if (useDurableCache)
		{
			cacheBucket.Set(channel, cacheVersion, computed);
		}

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

	/// <summary>
	/// Whether any route was ever registered that satisfies <paramref name="predicate"/>, whatever its
	/// module's presence predicate would decide and whether or not a later registration shadows it.
	/// </summary>
	/// <remarks>
	/// For a caller that must answer what the application <em>can</em> contain rather than what one
	/// resolution does contain — declaring an optional protocol capability, for instance, which happens
	/// once and cannot be revised per caller. Blind to presence, because an answer derived from one
	/// evaluation of the predicates is only as good as that evaluation's inputs and a predicate can
	/// depend on state the caller does not have; blind to shadowing, because a template registered
	/// twice resolves to a single route while the shadowed registration is still reachable from any
	/// resolution that excludes the shadowing module. Yields a verdict rather than the routes so that
	/// no caller can project command names through it, and resolves, documents and validates nothing.
	/// </remarks>
	internal bool AnyRegisteredRoute(Func<RouteDefinition, bool> predicate)
	{
		foreach (var route in _routes)
		{
			if (predicate(route))
			{
				return true;
			}
		}

		return false;
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

	/// <summary>
	/// Whether the current invocation runs inside an interactive session. Reads the ambient runtime
	/// state pushed for the session, so it stays correct for a protocol-passthrough command, which
	/// carries no scope tokens whatever the mode.
	/// </summary>
	internal bool IsInteractiveSession => _runtimeState.Value?.IsInteractiveSession == true;

	internal RuntimeStateScope PushRuntimeState(
		IServiceProvider serviceProvider,
		bool isInteractiveSession,
		IServiceProvider? presenceServiceProvider = null)
	{
		var previous = _runtimeState.Value;
		_runtimeState.Value = new InvocationRuntimeState(
			serviceProvider,
			isInteractiveSession,
			presenceServiceProvider);
		return new RuntimeStateScope(_runtimeState, previous);
	}

	private static string ResolveEntryAssemblyName()
	{
		var entryAssembly = Assembly.GetEntryAssembly();
		return entryAssembly?.GetName().Name
			?? Assembly.GetExecutingAssembly().GetName().Name
			?? string.Empty;
	}

	[SuppressMessage(
		"Maintainability",
		"MA0051:Method is too long",
		Justification = "Levenshtein implementation keeps pooling and fast-path logic explicit for readability.")]
	internal static int ComputeLevenshteinDistance(string source, string target)
	{
		if (string.Equals(source, target, StringComparison.Ordinal))
		{
			return 0;
		}

		if (source.Length == 0)
		{
			return target.Length;
		}

		if (target.Length == 0)
		{
			return source.Length;
		}

		if (target.Length > source.Length)
		{
			(source, target) = (target, source);
		}

		var width = target.Length + 1;
		const int stackThreshold = 256;
		int[]? rentedPrevious = null;
		int[]? rentedCurrent = null;
		Span<int> previousRow = width <= stackThreshold
			? stackalloc int[width]
			: (rentedPrevious = ArrayPool<int>.Shared.Rent(width)).AsSpan(0, width);
		Span<int> currentRow = width <= stackThreshold
			? stackalloc int[width]
			: (rentedCurrent = ArrayPool<int>.Shared.Rent(width)).AsSpan(0, width);
		try
		{
			for (var column = 0; column < width; column++)
			{
				previousRow[column] = column;
			}

			for (var row = 1; row <= source.Length; row++)
			{
				currentRow[0] = row;
				var sourceChar = source[row - 1];
				for (var column = 1; column < width; column++)
				{
					var cost = sourceChar == target[column - 1] ? 0 : 1;
					var deletion = previousRow[column] + 1;
					var insertion = currentRow[column - 1] + 1;
					var substitution = previousRow[column - 1] + cost;
					currentRow[column] = Math.Min(Math.Min(deletion, insertion), substitution);
				}

				var nextPrevious = currentRow;
				currentRow = previousRow;
				previousRow = nextPrevious;
			}

			return previousRow[width - 1];
		}
		finally
		{
			if (rentedPrevious is not null)
			{
				ArrayPool<int>.Shared.Return(rentedPrevious);
			}

			if (rentedCurrent is not null)
			{
				ArrayPool<int>.Shared.Return(rentedCurrent);
			}
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
			[typeof(IGlobalOptionsAccessor)] = _globalOptionsSnapshot,
			[typeof(IReplSessionState)] = new InMemoryReplSessionState(),
			[typeof(IReplInteractionChannel)] = new ConsoleInteractionChannel(
				_options.Interaction, _options.Output,
				handlers: [new RichPromptInteractionHandler(_options.Output)]),
			[typeof(IHistoryProvider)] = _options.Interactive.HistoryProvider ?? new InMemoryHistoryProvider(),
			[typeof(IReplKeyReader)] = new ConsoleKeyReader(),
			[typeof(IReplSessionInfo)] = new LiveSessionInfo(),
			[typeof(IReplIoContext)] = new LiveReplIoContext(),
			[typeof(TimeProvider)] = TimeProvider.System,
		};
		return new DefaultServiceProvider(defaults);
	}

	internal static bool IsHelpToken(string token) =>
		string.Equals(token, "help", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(token, "?", StringComparison.Ordinal);


	internal string BuildHumanHelp(IReadOnlyList<string> scopeTokens)
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
			CurrentServiceProvider,
			_options.AmbientCommands,
			renderWidth: settings.Width,
			useAnsi: settings.UseAnsi,
			palette: settings.Palette);
	}


	private readonly record struct ModuleRegistration(
		int ModuleId,
		Func<ModulePresenceContext, bool> IsPresent);

	/// <param name="ServiceProvider">Resolves handler arguments for this invocation.</param>
	/// <param name="IsInteractiveSession">Whether the invocation belongs to an interactive session.</param>
	/// <param name="PresenceServiceProvider">
	/// Resolves module presence predicates, when they must be decided from something other than what
	/// binds the handler. A host that publishes a command catalog has to keep the two apart: what it
	/// advertised was decided from one view of the world, and re-deciding at execution from another
	/// makes an advertised command unreachable. <see langword="null"/> — the default everywhere except
	/// that case — means presence and binding share one provider, as they always have.
	/// </param>
	internal readonly record struct InvocationRuntimeState(
		IServiceProvider ServiceProvider,
		bool IsInteractiveSession,
		IServiceProvider? PresenceServiceProvider = null);

	private sealed class RoutingCacheEntry(long version, ActiveRoutingGraph graph)
	{
		public long Version { get; } = version;

		public ActiveRoutingGraph Graph { get; } = graph;
	}

	internal sealed class RuntimeStateScope(
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

			// Resolve IServiceProvider to the provider itself: framework-injected parameters
			// (e.g. the completion bridge handler) request it, and a null here would silently
			// disable shell-scoped WithCompletion providers for CoreReplApp-only apps. MS DI
			// self-registers it; the built-in provider must match that contract.
			if (serviceType == typeof(IServiceProvider))
			{
				return this;
			}

			return services.TryGetValue(serviceType, out var service) ? service : null;
		}
	}

}
