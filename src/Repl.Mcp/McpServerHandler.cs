using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Documentation;
using Repl.Interaction;
using Repl.Internal.Options;

namespace Repl.Mcp;

/// <summary>
/// Orchestrates the MCP server lifecycle: builds the documentation model,
/// generates MCP primitives, and runs the server until cancellation.
/// </summary>
internal sealed class McpServerHandler
{
	private const string DiscoverToolsName = "discover_tools";
	private const string CallToolName = "call_tool";
	private static readonly IDictionary<string, JsonElement> EmptyArguments =
		new Dictionary<string, JsonElement>(StringComparer.Ordinal);
	private static readonly string[] CompatibilityCallRequiredProperties = ["name"];

	private readonly ICoreReplApp _app;
	private readonly ReplMcpServerOptions _options;
	private readonly IServiceProvider _services;
	private readonly TimeProvider _timeProvider;
	private readonly char _separator;
	private readonly McpRequestServerAccessor _requestServers = new();
	private readonly McpSamplingService _sampling;
	private readonly McpElicitationService _elicitation;
	private readonly McpFeedbackService _feedback;
	// Context for work that belongs to no MCP session: eager fail-fast validation, the pre-built
	// catalog behind BuildMcpServerOptions, and the snapshot test seams. Each of those used to mint
	// its own throwaway context — five roots services and five never-disposed semaphores dropped on
	// the floor. It shares the handler's lifetime, so nothing disposes it either; unlike a session
	// context there is never more than one.
	private readonly McpSessionContext _catalogContext;
	private readonly Lock _refreshLock = new();
	private readonly Lock _attachLock = new();

	// Global routing version: bumped by InvalidateRouting for every session; each session's
	// context caches the snapshot it built at a given version.
	private SnapshotVersionState _snapshotState = new(Version: 1, LastVisibilityRetractionVersion: 0);
	// One handler can serve several concurrent sessions; everything session-owned lives in
	// McpSessionContext, and this list (guarded by _attachLock) tracks every ACTIVE session
	// for server-initiated notifications and subscription lifetime.
	private readonly List<McpSessionContext> _sessions = [];
	private EventHandler<RoutingInvalidatedEventArgs>? _routingChangedHandler;
	private ITimer? _debounceTimer;
	private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(100);

	// Discovery-change signals. These collections stay EMPTY and never contribute a primitive to a
	// list response: they exist only so the SDK's own fan-out runs, because that is the only code
	// with access to the subscription registry. On 2026-07-28 it delivers each notification type
	// ONLY to clients that requested it through subscriptions/listen, over that request's stream and
	// tagged with its id, while still broadcasting session-wide to initialize-era clients. Every
	// McpServer built from the options subscribes on construction and unsubscribes on dispose, which
	// is what makes one shared instance correct across concurrent connections.
	private readonly McpServerPrimitiveCollection<McpServerTool> _toolListChanged = new();
	private readonly McpServerResourceCollection _resourceListChanged = new();
	private readonly McpServerPrimitiveCollection<McpServerPrompt> _promptListChanged = new();

	public McpServerHandler(
		ICoreReplApp app,
		ReplMcpServerOptions options,
		IServiceProvider services)
	{
		_app = app;
		_options = options;
		_services = services;
		_timeProvider = services.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;
		_separator = McpToolNameFlattener.ResolveSeparator(options.ToolNamingSeparator);
		// Sampling/elicitation/feedback are stateless (they resolve the request-bound server
		// through the accessor) and safely shared; roots and everything else session-owned
		// is created per session in CreateSessionContext.
		_sampling = new McpSamplingService(_requestServers);
		_elicitation = new McpElicitationService(_requestServers);
		_feedback = new McpFeedbackService(_requestServers);
		_catalogContext = CreateSessionContext();
	}

	private McpSessionContext CreateSessionContext()
	{
		var roots = new McpClientRootsService(_app, _requestServers);
		var overlayServices = new Dictionary<Type, object>
		{
			[typeof(IMcpClientRoots)] = roots,
			[typeof(IMcpSampling)] = _sampling,
			[typeof(IMcpElicitation)] = _elicitation,
			[typeof(IMcpFeedback)] = _feedback,
		};
		var context = new McpSessionContext(roots, new McpServiceProviderOverlay(_services, overlayServices));
		// The context rides in its own overlay so request handlers can recover their
		// originating session through the server's provider (the dictionary is captured by
		// reference, making this two-phase registration safe).
		overlayServices[typeof(McpSessionContext)] = context;
		return context;
	}

	/// <summary>
	/// Recovers the session a request belongs to, through the provider handed to
	/// <c>McpServer.Create</c> — even a destination-bound per-request server exposes its session's
	/// services.
	/// </summary>
	/// <remarks>
	/// A pure lookup on purpose. This used to fall back to one lazily created context shared by every
	/// caller that missed the lookup, which made <c>_sessions</c> permanently non-empty — so the
	/// routing subscription and its debounce timer could never be released — and latched a
	/// destination-bound per-request server as if it were a session server. The fallback was also
	/// unreachable: the only server built without a session provider comes from
	/// <see cref="BuildStaticServerOptions"/>, whose pre-built primitives never route through here.
	/// </remarks>
	private static McpSessionContext ResolveContext(McpServer? requestServer) =>
		requestServer?.Services?.GetService(typeof(McpSessionContext)) as McpSessionContext
		?? throw new InvalidOperationException(
			"An MCP request reached a Repl handler without its session context. Handlers are only "
			+ "registered by BuildDynamicServerOptions, whose server is always created with the "
			+ "session's own service provider.");

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2026",
		Justification = "MCP server handler runs in a context where all types are preserved.")]
	public async Task RunAsync(IReplIoContext io, CancellationToken ct)
	{
		var serverOptions = BuildDynamicServerOptions();
		var serverName = serverOptions.ServerInfo?.Name ?? "repl-mcp-server";
		var transport = _options.TransportFactory is { } factory
			? factory(serverName, io)
			: new StdioServerTransport(serverName);
		try
		{
			using var context = CreateSessionContext();
			var server = McpServer.Create(transport, serverOptions, serviceProvider: context.Services);
			AttachSession(context, server);

			try
			{
				await server.RunAsync(ct).ConfigureAwait(false);
			}
			finally
			{
				DetachSession(context);
				await server.DisposeAsync().ConfigureAwait(false);
			}
		}
		finally
		{
			await transport.DisposeAsync().ConfigureAwait(false);
		}
	}

	internal McpServerOptions BuildDynamicServerOptions()
	{
		var serverName = _options.ServerName ?? ResolveAppName() ?? "repl-mcp-server";
		var serverVersion = _options.ServerVersion ?? "1.0.0";

		// Preserve fail-fast validation when every command belongs to MCP. With a filter, defer
		// projection until the root-aware snapshot so the user predicate is not invoked eagerly and
		// then repeated for the same commands during the first discovery request.
		if (_options.CommandFilter is null)
		{
			_ = CreateDocumentationModel(_catalogContext.Services);
		}

		return new McpServerOptions
		{
			ServerInfo = new Implementation { Name = serverName, Version = serverVersion },
			Capabilities = BuildCapabilities(),
			Handlers = new McpServerHandlers
			{
				ListToolsHandler = ListToolsAsync,
				CallToolHandler = CallToolAsync,
				ListResourcesHandler = ListResourcesAsync,
				ListResourceTemplatesHandler = ListResourceTemplatesAsync,
				ReadResourceHandler = ReadResourceAsync,
				ListPromptsHandler = ListPromptsAsync,
				GetPromptHandler = GetPromptAsync,
			},
			// Empty on purpose: collections augment the handlers rather than replace them, so the
			// tool graph still comes entirely from the handlers above. See the field declarations.
			ToolCollection = _toolListChanged,
			ResourceCollection = _resourceListChanged,
			PromptCollection = _promptListChanged,
		};
	}

	internal McpServerOptions BuildStaticServerOptions()
	{
		var serverName = _options.ServerName ?? ResolveAppName() ?? "repl-mcp-server";
		var serverVersion = _options.ServerVersion ?? "1.0.0";
		var snapshot = BuildSnapshotCore(_catalogContext);

		return new McpServerOptions
		{
			ServerInfo = new Implementation { Name = serverName, Version = serverVersion },
			Capabilities = BuildCapabilities(),
			ToolCollection = ToCollection(snapshot.Tools),
			ResourceCollection = ToResourceCollection(snapshot.Resources),
			PromptCollection = ToCollection(snapshot.Prompts),
		};
	}

	internal McpGeneratedSnapshot BuildSnapshotForTests() => BuildSnapshotCore(_catalogContext);

	internal async Task<McpGeneratedSnapshot> BuildSnapshotForTestsAsync(CancellationToken cancellationToken = default) =>
		await GetSnapshotAsync(_catalogContext, cancellationToken).ConfigureAwait(false);

	private string? ResolveAppName()
	{
		var coreApp = _app as CoreReplApp
			?? throw new InvalidOperationException("MCP server handler requires CoreReplApp.");
		return coreApp.BuildDocumentationApp().Name;
	}

	private async ValueTask<ListToolsResult> ListToolsAsync(
		RequestContext<ListToolsRequestParams> request,
		CancellationToken cancellationToken)
	{
		BindRequest(request);
		var context = ResolveContext(request.Server);
		var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);

		if (_options.DynamicToolCompatibility == DynamicToolCompatibilityMode.DiscoverAndCallShim
			&& context.TryClaimCompatibilityIntro())
		{
			SignalToolListChanged();
			return new ListToolsResult
			{
				Tools =
				[
					CreateCompatibilityDiscoverTool(),
					CreateCompatibilityCallTool(),
				],
			};
		}

		return new ListToolsResult
		{
			Tools = [.. snapshot.Tools.Select(static tool => tool.ProtocolTool)],
		};
	}

	private async ValueTask<CallToolResult> CallToolAsync(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken)
	{
		BindRequest(request);
		var context = ResolveContext(request.Server);
		var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
		IDictionary<string, JsonElement> arguments = request.Params.Arguments ?? EmptyArguments;
		var toolName = request.Params.Name ?? string.Empty;
		var progressToken = request.Params.ProgressToken;

		if (_options.DynamicToolCompatibility == DynamicToolCompatibilityMode.DiscoverAndCallShim)
		{
			if (string.Equals(toolName, DiscoverToolsName, StringComparison.Ordinal))
			{
				return BuildDiscoverToolsResult(snapshot);
			}

			if (string.Equals(toolName, CallToolName, StringComparison.Ordinal))
			{
				return await InvokeCompatibilityToolAsync(snapshot, arguments, request.Server, progressToken, cancellationToken)
					.ConfigureAwait(false);
			}
		}

		return await snapshot.Adapter.InvokeAsync(
			toolName,
			arguments,
			request.Server,
			progressToken,
			cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<ListResourcesResult> ListResourcesAsync(
		RequestContext<ListResourcesRequestParams> request,
		CancellationToken cancellationToken)
	{
		BindRequest(request);
		var context = ResolveContext(request.Server);
		var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
		return new ListResourcesResult
		{
			Resources =
			[
				.. snapshot.Resources
					.Where(static resource => !resource.IsTemplated && resource.ProtocolResource is not null)
					.Select(static resource => resource.ProtocolResource!),
			],
		};
	}

	private async ValueTask<ListResourceTemplatesResult> ListResourceTemplatesAsync(
		RequestContext<ListResourceTemplatesRequestParams> request,
		CancellationToken cancellationToken)
	{
		BindRequest(request);
		var context = ResolveContext(request.Server);
		var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
		return new ListResourceTemplatesResult
		{
			ResourceTemplates =
			[
				.. snapshot.Resources
					.Where(static resource => resource.IsTemplated)
					.Select(static resource => resource.ProtocolResourceTemplate),
			],
		};
	}

	private async ValueTask<ReadResourceResult> ReadResourceAsync(
		RequestContext<ReadResourceRequestParams> request,
		CancellationToken cancellationToken)
	{
		BindRequest(request);
		var context = ResolveContext(request.Server);
		var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
		var uri = request.Params.Uri ?? string.Empty;
		var resource = snapshot.Resources.FirstOrDefault(candidate => candidate.IsMatch(uri));
		if (resource is null)
		{
			throw new McpException($"Unknown resource: {uri}");
		}

		return await resource.ReadAsync(request, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<ListPromptsResult> ListPromptsAsync(
		RequestContext<ListPromptsRequestParams> request,
		CancellationToken cancellationToken)
	{
		BindRequest(request);
		var context = ResolveContext(request.Server);
		var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
		return new ListPromptsResult
		{
			Prompts = [.. snapshot.Prompts.Select(static prompt => prompt.ProtocolPrompt)],
		};
	}

	private async ValueTask<GetPromptResult> GetPromptAsync(
		RequestContext<GetPromptRequestParams> request,
		CancellationToken cancellationToken)
	{
		BindRequest(request);
		var context = ResolveContext(request.Server);
		var snapshot = await GetSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
		var promptName = request.Params.Name ?? string.Empty;
		var prompt = snapshot.Prompts.FirstOrDefault(candidate =>
			string.Equals(candidate.ProtocolPrompt.Name, promptName, StringComparison.OrdinalIgnoreCase));
		if (prompt is null)
		{
			throw new McpException($"Unknown prompt: {promptName}");
		}

		return await prompt.GetAsync(request, cancellationToken).ConfigureAwait(false);
	}

	// The snapshot is SESSION state: the tool graph can be gated on session capabilities
	// (roots, module presence predicates), so each context caches its own build against
	// the handler-global routing version.
	private async ValueTask<McpGeneratedSnapshot> GetSnapshotAsync(
		McpSessionContext context,
		CancellationToken cancellationToken)
	{
		var snapshotVersion = Volatile.Read(ref _snapshotState).Version;
		if (context.SnapshotCache is { } cached && cached.Version == snapshotVersion)
		{
			return cached.Snapshot;
		}

		await context.SnapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			snapshotVersion = Volatile.Read(ref _snapshotState).Version;
			if (context.SnapshotCache is { } refreshed && refreshed.Version == snapshotVersion)
			{
				return refreshed.Snapshot;
			}

			var previousSnapshot = context.SnapshotCache?.Snapshot;
			try
			{
				return await BuildCurrentSnapshotAsync(context, snapshotVersion, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (HiddenRequiredOptionException)
			{
				ThrowSanitizedIfAClientAlreadyHasASchema(previousSnapshot);
				throw;
			}
			catch (Exception) when (
				previousSnapshot is not null
				&& Volatile.Read(ref _snapshotState).LastVisibilityRetractionVersion
					<= (context.SnapshotCache?.Version ?? McpSessionContext.SnapshotCacheEntry.StaleVersion))
			{
				// Preserve availability for transient projection failures, but republish as stale so the
				// next request retries without requiring another routing mutation.
				context.PublishStaleSnapshot(previousSnapshot);
				return previousSnapshot;
			}
		}
		finally
		{
			context.SnapshotGate.Release();
		}
	}

	// No snapshot has ever been served: a cold-start configuration error, not a runtime retraction
	// reaching an already-connected client. Let the caller's rethrow carry the detailed exception so
	// the operator sees exactly which option and route are misconfigured.
	//
	// Once a client HAS a working schema, the same failure must fail closed (returning the previous
	// snapshot would keep advertising an option the app explicitly hid), but the exception's own
	// message names that option's target, rendered token and route — precisely the identity hiding it
	// was meant to withhold — so it must not reach the client verbatim. A generic McpException (the
	// pattern this handler already uses for other client-facing failures) reports the failure without
	// disclosing what triggered it.
	private static void ThrowSanitizedIfAClientAlreadyHasASchema(McpGeneratedSnapshot? previousSnapshot)
	{
		if (previousSnapshot is not null)
		{
			throw new McpException("Tool discovery is temporarily unavailable due to a server configuration error.");
		}
	}

	private async ValueTask<McpGeneratedSnapshot> BuildCurrentSnapshotAsync(
		McpSessionContext context,
		long snapshotVersion,
		CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await context.Roots.GetAsync(cancellationToken).ConfigureAwait(false);
			var built = BuildSnapshotCore(context);
			var observedState = Volatile.Read(ref _snapshotState);

			// Version and retraction watermark are one atomically published state. A reader can
			// therefore never observe the new version without the visibility retraction that caused it.
			// If that state appeared after projection started, discard the result and rebuild.
			if (observedState.LastVisibilityRetractionVersion > snapshotVersion)
			{
				snapshotVersion = observedState.Version;
				continue;
			}

			// One publication either way: a build that raced a routing bump is still serve-able, but
			// is marked stale so the next request rebuilds it.
			if (observedState.Version == snapshotVersion)
			{
				context.PublishSnapshot(built, snapshotVersion);
			}
			else
			{
				context.PublishStaleSnapshot(built);
			}
			return built;
		}
	}

	private McpGeneratedSnapshot BuildSnapshotCore(McpSessionContext context)
	{
		// Project once here so tools/list, tools/call and prompts/list all read the same option list.
		var model = McpAutomationProjection.Apply(CreateDocumentationModel(context.Services));
		var adapter = new McpToolAdapter(_app, _options, context.Services, _requestServers);
		var commandsByPath = model.Commands.ToDictionary(
			command => command.Path,
			command => command,
			StringComparer.OrdinalIgnoreCase);
		var tools = GenerateAllTools(model, adapter, _separator, commandsByPath);
		ValidateCompatibilityToolNames(tools);
		var resources = GenerateResources(model, adapter, _separator, commandsByPath, context.Services);
		var prompts = CollectPrompts(model, adapter, _separator);
		return new McpGeneratedSnapshot(adapter, tools, resources, prompts);
	}

	// The documentation model resolves module-presence predicates against the SESSION's
	// services (e.g. IMcpClientRoots), so the model — and everything generated from it —
	// reflects the capabilities of the session it is built for.
	private ReplDocumentationModel CreateDocumentationModel(IServiceProvider sessionServices)
	{
		var coreApp = _app as CoreReplApp
			?? throw new InvalidOperationException("MCP server handler requires CoreReplApp.");

		var previousProgrammatic = ReplSessionIO.IsProgrammatic;
		ReplSessionIO.IsProgrammatic = true;
		try
		{
			return coreApp.CreateDocumentationModel(
				CreateDiscoveryServices(sessionServices),
				IsMcpCandidateBeforeValidation);
		}
		finally
		{
			ReplSessionIO.IsProgrammatic = previousProgrammatic;
		}
	}

	// Every MCP tool invocation overlays a concrete interaction channel before entering the
	// binder. Discovery must expose that guaranteed fallback even when the caller did not supply
	// a base provider (or supplied one without the channel).
	private McpServiceProviderOverlay CreateDiscoveryServices(IServiceProvider sessionServices) =>
		new(
			sessionServices,
			new Dictionary<Type, object>
			{
				[typeof(IReplInteractionChannel)] = new McpInteractionChannel(
					new Dictionary<string, string>(StringComparer.Ordinal),
					_options.InteractivityMode),
			});

	private void ValidateCompatibilityToolNames(IReadOnlyList<McpServerTool> tools)
	{
		if (_options.DynamicToolCompatibility != DynamicToolCompatibilityMode.DiscoverAndCallShim)
		{
			return;
		}

		var collision = tools
			.Select(static tool => tool.ProtocolTool.Name)
			.FirstOrDefault(name =>
				string.Equals(name, DiscoverToolsName, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(name, CallToolName, StringComparison.OrdinalIgnoreCase));
		if (collision is not null)
		{
			throw new InvalidOperationException(
				$"MCP tool name collision: '{collision}' is reserved by DynamicToolCompatibility mode.");
		}
	}

	// Request-level binding: capability services resolve the flowing request through the AsyncLocal
	// accessor, so concurrent requests (SDK 2.0 creates one destination-bound McpServer per request)
	// cannot cross-wire each other's client capabilities. The whole request is bound, not just its
	// server, because 2026-07-28 carries the client's capabilities and log level in per-request
	// _meta. Session-level concerns are handled by AttachSession (RunAsync).
	private void BindRequest(MessageContext request) => _requestServers.BindRequest(request);

	// Session-level attach: routing-change notifications and the roots list-changed
	// handler belong to the session servers, registered once per session — never to the
	// per-request destination wrappers.
	private void AttachSession(McpSessionContext context, McpServer server)
	{
		lock (_attachLock)
		{
			_sessions.Add(context);
			EnsureRoutingSubscription();
			EnsureRootsNotificationHandler(server, context.Roots);
		}
	}

	internal sealed record SnapshotVersionState(
		long Version,
		long LastVisibilityRetractionVersion);

	private void EnsureRoutingSubscription()
	{
		if (_routingChangedHandler is not null || _app is not CoreReplApp coreApp)
		{
			return;
		}

		var weakSelf = new WeakReference<McpServerHandler>(this);
		EventHandler<RoutingInvalidatedEventArgs>? handler = null;
		handler = (_, args) =>
		{
			if (!weakSelf.TryGetTarget(out var target))
			{
				coreApp.RoutingInvalidatedDetailed -= handler;
				return;
			}

			target.OnRoutingInvalidated(args.IsVisibilityRetraction);
		};

		_routingChangedHandler = handler;
		coreApp.RoutingInvalidatedDetailed += handler;
	}

	// Removes a closing session and repairs the shared state: the accessor's session
	// fallback moves to a surviving session, and the routing subscription is dropped only
	// when the LAST session ends — a first-session close must not silence the others.
	private void DetachSession(McpSessionContext context)
	{
		lock (_attachLock)
		{
			_sessions.Remove(context);
			if (_sessions.Count == 0)
			{
				UnsubscribeFromRoutingChanges();
			}
		}
	}

	private static void EnsureRootsNotificationHandler(McpServer server, McpClientRootsService roots)
	{
		var weakSelf = new WeakReference<McpClientRootsService>(roots);
		// Roots is deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005) but hosts still send
		// this notification; Repl keeps supporting it until the SDK removes the surface (#51).
#pragma warning disable MCP9005
		_ = server.RegisterNotificationHandler(
			NotificationMethods.RootsListChangedNotification,
			(_, _) =>
			{
				if (weakSelf.TryGetTarget(out var target))
				{
					target.HandleRootsListChanged();
				}

				return ValueTask.CompletedTask;
			});
#pragma warning restore MCP9005
	}

	internal static SnapshotVersionState PublishSnapshotInvalidation(
		ref SnapshotVersionState snapshotState,
		bool isVisibilityRetraction,
		Action<SnapshotVersionState>? beforePublish = null)
	{
		SnapshotVersionState currentState;
		SnapshotVersionState invalidatedState;
		do
		{
			currentState = Volatile.Read(ref snapshotState);
			var invalidatedVersion = currentState.Version + 1;
			invalidatedState = new SnapshotVersionState(
				invalidatedVersion,
				isVisibilityRetraction
					? invalidatedVersion
					: currentState.LastVisibilityRetractionVersion);
			beforePublish?.Invoke(invalidatedState);
		}
		while (!ReferenceEquals(
			Interlocked.CompareExchange(ref snapshotState, invalidatedState, currentState),
			currentState));

		return invalidatedState;
	}

	private void OnRoutingInvalidated(bool isVisibilityRetraction)
	{
		PublishSnapshotInvalidation(ref _snapshotState, isVisibilityRetraction);

		if (_options.DynamicToolCompatibility == DynamicToolCompatibilityMode.DiscoverAndCallShim)
		{
			// Every active session re-serves its compatibility intro after a routing change.
			lock (_attachLock)
			{
				foreach (var session in _sessions)
				{
					session.ResetCompatibilityIntro();
				}
			}
		}

		lock (_refreshLock)
		{
			_debounceTimer?.Dispose();
			_debounceTimer = _timeProvider.CreateTimer(
				_ => SignalDiscoveryChanged(),
				state: null,
				dueTime: DebounceDelay,
				period: Timeout.InfiniteTimeSpan);
		}
	}

	private void SignalDiscoveryChanged()
	{
		_toolListChanged.Clear();
		_resourceListChanged.Clear();
		_promptListChanged.Clear();
	}

	// Clearing an already-empty primitive collection raises its Changed event without mutating
	// anything, which is what lets an empty collection act as a pure signal. That the event fires
	// unconditionally is NOT documented on Clear(), so it is pinned by
	// Given_McpSubscriptions.When_ClearingAnEmptyCollection_Then_ChangedStillFires: if a future SDK
	// turns Clear() into a no-op, that test fails loudly instead of discovery notifications silently
	// disappearing.
	private void SignalToolListChanged() => _toolListChanged.Clear();

	private void UnsubscribeFromRoutingChanges()
	{
		if (_routingChangedHandler is not null && _app is CoreReplApp coreApp)
		{
			coreApp.RoutingInvalidatedDetailed -= _routingChangedHandler;
			_routingChangedHandler = null;
		}

		lock (_refreshLock)
		{
			_debounceTimer?.Dispose();
			_debounceTimer = null;
		}
	}

	private ServerCapabilities BuildCapabilities()
	{
		// Logging is deprecated by MCP spec 2026-07-28 (SEP-2577, MCP9005) but the feedback
		// bridge still routes through logging notifications for current hosts; Repl keeps
		// advertising it until the SDK removes the surface (#51).
#pragma warning disable MCP9005
		var capabilities = new ServerCapabilities
		{
			Logging = new LoggingCapability(),
			Tools = new ToolsCapability { ListChanged = true },
			Resources = new ResourcesCapability { ListChanged = true },
			Prompts = new PromptsCapability { ListChanged = true },
		};
#pragma warning restore MCP9005

		if (_options.EnableApps || HasMcpAppResources())
		{
#pragma warning disable MCPEXP001
			capabilities.Extensions = new Dictionary<string, object>(StringComparer.Ordinal)
			{
				[McpAppMetadata.ExtensionName] = new JsonObject
				{
					["mimeTypes"] = JsonSerializer.SerializeToNode(
						new[] { McpAppValidation.ResourceMimeType },
						McpJsonContext.Default.StringArray),
				},
			};
#pragma warning restore MCPEXP001
		}

		return capabilities;
	}

	private bool HasMcpAppResources()
	{
		if (_options.UiResources.Count > 0)
		{
			return true;
		}

		var coreApp = _app as CoreReplApp
			?? throw new InvalidOperationException("MCP server handler requires CoreReplApp.");
		var previousProgrammatic = ReplSessionIO.IsProgrammatic;
		ReplSessionIO.IsProgrammatic = true;
		try
		{
			var sessionServices = _catalogContext.Services;
			using var runtimeStateScope = coreApp.PushRuntimeState(
				CreateDiscoveryServices(sessionServices),
				isInteractiveSession: false);
			var activeGraph = coreApp.ResolveActiveRoutingGraph();
			var commands = coreApp.ResolveDiscoverableRoutes(
				activeGraph.Routes,
				activeGraph.Contexts,
				Array.Empty<string>(),
				StringComparison.OrdinalIgnoreCase);

			// This capability probe historically ignores CommandFilter. Inspect route metadata directly
			// so the filter remains a once-per-snapshot predicate and excluded invalid CLI routes are not
			// documented or validated merely to decide whether the optional Apps extension is advertised.
			return commands.Any(static route =>
				!route.Command.IsHidden
				&& (route.Command.Metadata.ContainsKey(McpAppMetadata.ResourceMetadataKey)
					|| route.Command.Metadata.ContainsKey(McpAppMetadata.CommandMetadataKey)));
		}
		finally
		{
			ReplSessionIO.IsProgrammatic = previousProgrammatic;
		}
	}

	private static Tool CreateCompatibilityDiscoverTool() => new()
	{
		Name = DiscoverToolsName,
		Description = "Discover the current dynamic MCP tool list.",
		InputSchema = JsonSerializer.SerializeToElement(
			new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject(),
				["additionalProperties"] = false,
			},
			McpJsonContext.Default.JsonObject),
		Annotations = new ToolAnnotations { ReadOnlyHint = true },
	};

	private static Tool CreateCompatibilityCallTool() => new()
	{
		Name = CallToolName,
		Description = "Call a dynamic MCP tool by name when the client cannot refresh the tool list.",
		InputSchema = JsonSerializer.SerializeToElement(
			new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["name"] = new JsonObject
					{
						["type"] = "string",
						["description"] = "The real dynamic tool name to invoke.",
					},
					["arguments"] = new JsonObject
					{
						["type"] = "object",
						["description"] = "The arguments to pass to the real dynamic tool.",
						["additionalProperties"] = true,
					},
				},
				["required"] = new JsonArray(CompatibilityCallRequiredProperties.Select(static property => JsonValue.Create(property)).ToArray()),
				["additionalProperties"] = false,
			},
			McpJsonContext.Default.JsonObject),
	};

	private static CallToolResult BuildDiscoverToolsResult(McpGeneratedSnapshot snapshot)
	{
		var tools = snapshot.Tools.Select(static tool => tool.ProtocolTool).ToArray();
		var structuredContent = JsonSerializer.SerializeToElement(tools, McpJsonContext.Default.ToolArray);

		return new CallToolResult
		{
			Content = [new TextContentBlock { Text = $"Discovered {tools.Length} dynamic tool(s)." }],
			StructuredContent = structuredContent,
			IsError = false,
		};
	}

	private static async ValueTask<CallToolResult> InvokeCompatibilityToolAsync(
		McpGeneratedSnapshot snapshot,
		IDictionary<string, JsonElement> arguments,
		McpServer? server,
		ProgressToken? progressToken,
		CancellationToken cancellationToken)
	{
		if (!arguments.TryGetValue("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
		{
			return new CallToolResult
			{
				Content = [new TextContentBlock { Text = "Compatibility call_tool requires a string 'name' argument." }],
				IsError = true,
			};
		}

		var toolName = nameElement.GetString() ?? string.Empty;
		var toolArguments = ExtractCompatibilityArguments(arguments);
		return await snapshot.Adapter.InvokeAsync(
			toolName,
			toolArguments,
			server,
			progressToken,
			cancellationToken).ConfigureAwait(false);
	}

	private static IDictionary<string, JsonElement> ExtractCompatibilityArguments(
		IDictionary<string, JsonElement> arguments)
	{
		if (!arguments.TryGetValue("arguments", out var nestedArguments)
			|| nestedArguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
		{
			return EmptyArguments;
		}

		if (nestedArguments.ValueKind != JsonValueKind.Object)
		{
			return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["arguments"] = nestedArguments.Clone(),
			};
		}

		var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		foreach (var property in nestedArguments.EnumerateObject())
		{
			result[property.Name] = property.Value.Clone();
		}

		return result;
	}

	// ── Tool generation ────────────────────────────────────────────────

	/// <summary>
	/// Generates all tools: regular commands + resource fallback tools + prompt fallback tools.
	/// Resources and prompts are always also exposed as tools so agents that don't support
	/// the native primitives (~61% for resources, ~62% for prompts) can still access them.
	/// Use <see cref="ReplMcpServerOptions.ResourceFallbackToTools"/> and
	/// <see cref="ReplMcpServerOptions.PromptFallbackToTools"/> to disable.
	/// </summary>
	private List<McpServerTool> GenerateAllTools(
		ReplDocumentationModel model,
		McpToolAdapter adapter,
		char separator,
		Dictionary<string, ReplDocCommand> commandsByPath)
	{
		var tools = new List<McpServerTool>();
		var nameSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var command in model.Commands)
		{
			if (!IsToolCandidate(command))
			{
				continue;
			}

			if (command.IsPrompt)
			{
				continue;
			}

			if (command.IsResource && command.Annotations?.ReadOnly != true)
			{
				continue;
			}

			if (TryGetAppResourceOptions(command, out var appResourceOptions))
			{
				AddMcpAppLauncherTool(command, appResourceOptions, tools, nameSet, adapter, separator);
				continue;
			}

			AddTool(command, tools, nameSet, adapter, separator);
		}

		if (_options.ResourceFallbackToTools)
		{
			foreach (var resource in model.Resources)
			{
				if (commandsByPath.TryGetValue(resource.Path, out var docCommand)
					&& IsToolCandidate(docCommand))
				{
					AddTool(docCommand, tools, nameSet, adapter, separator);
				}
			}
		}

		if (_options.PromptFallbackToTools)
		{
			foreach (var command in model.Commands)
			{
				if (command.IsPrompt && IsToolCandidate(command))
				{
					AddTool(command, tools, nameSet, adapter, separator);
				}
			}
		}

		return tools;
	}

	private static void AddTool(
		ReplDocCommand command,
		List<McpServerTool> tools,
		Dictionary<string, string> nameSet,
		McpToolAdapter adapter,
		char separator)
	{
		var toolName = TryReserveToolName(command, nameSet, separator);
		if (toolName is null)
		{
			return;
		}

		adapter.RegisterRoute(toolName, command);
		tools.Add(new ReplMcpServerTool(command, toolName, adapter));
	}

	private static void AddMcpAppLauncherTool(
		ReplDocCommand command,
		McpAppCommandResourceOptions appResourceOptions,
		List<McpServerTool> tools,
		Dictionary<string, string> nameSet,
		McpToolAdapter adapter,
		char separator)
	{
		var toolName = TryReserveToolName(command, nameSet, separator);
		if (toolName is null)
		{
			return;
		}

		adapter.RegisterStaticResult(
			toolName,
			ReplMcpAppLauncherTool.BuildFallbackTextCore(command, appResourceOptions));
		tools.Add(new ReplMcpAppLauncherTool(command, toolName, appResourceOptions));
	}

	private static string? TryReserveToolName(
		ReplDocCommand command,
		Dictionary<string, string> nameSet,
		char separator)
	{
		var toolName = McpToolNameFlattener.Flatten(command.Path, separator);
		if (nameSet.TryGetValue(toolName, out var existingPath))
		{
			if (string.Equals(command.Path, existingPath, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			throw new InvalidOperationException(
				$"MCP tool name collision: '{toolName}' from routes '{existingPath}' and '{command.Path}'. " +
				"Consider a different ToolNamingSeparator or rename one of the commands.");
		}

		nameSet[toolName] = command.Path;
		return toolName;
	}

	// ── Resource generation ────────────────────────────────────────────

	private List<McpServerResource> GenerateResources(
		ReplDocumentationModel model,
		McpToolAdapter adapter,
		char separator,
		Dictionary<string, ReplDocCommand> commandsByPath,
		IServiceProvider sessionServices)
	{
		var resources = new List<McpServerResource>();
		var resourceMimeType = adapter.ForcedOutputMimeType;

		foreach (var resource in model.Resources)
		{
			commandsByPath.TryGetValue(resource.Path, out var docCommand);

			if (docCommand is not null && !IsToolCandidate(docCommand))
			{
				continue;
			}

			if (!_options.AutoPromoteReadOnlyToResources
				&& docCommand is not null
				&& !docCommand.IsResource
				&& docCommand.Annotations?.ReadOnly == true)
			{
				continue;
			}

			var resourceName = McpToolNameFlattener.Flatten(resource.Path, separator);
			if (TryGetAppResourceOptions(docCommand, out var appResourceOptions))
			{
				var mcpAppResource = new ReplMcpServerUiResource(
					docCommand!,
					resourceName,
					appResourceOptions,
					adapter);
				adapter.RegisterRoute(resourceName, docCommand!);
				resources.Add(mcpAppResource);
				continue;
			}

			var uriTemplate = McpToolNameFlattener.BuildResourceUri(resource.Path, _options.ResourceUriScheme);
			var mcpResource = new ReplMcpServerResource(
				resource,
				resourceName,
				uriTemplate,
				adapter,
				resourceMimeType);

			if (docCommand is not null)
			{
				adapter.RegisterRoute(resourceName, docCommand);
			}

			resources.Add(mcpResource);
		}

		foreach (var uiResource in _options.UiResources)
		{
			resources.Add(new McpAppResource(uiResource, sessionServices));
		}

		return resources;
	}

	private static bool TryGetAppResourceOptions(
		ReplDocCommand? command,
		[NotNullWhen(true)] out McpAppCommandResourceOptions? options)
	{
		if (command?.Metadata is not null
			&& command.Metadata.TryGetValue(McpAppMetadata.ResourceMetadataKey, out var value)
			&& value is McpAppCommandResourceOptions appResourceOptions)
		{
			options = appResourceOptions;
			return true;
		}

		options = null;
		return false;
	}

	// ── Prompt generation ──────────────────────────────────────────────

	private List<McpServerPrompt> CollectPrompts(
		ReplDocumentationModel model,
		McpToolAdapter adapter,
		char separator)
	{
		var prompts = new Dictionary<string, McpServerPrompt>(StringComparer.OrdinalIgnoreCase);
		var promptSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var command in model.Commands)
		{
			if (!command.IsPrompt || !IsToolCandidate(command))
			{
				continue;
			}

			var promptName = McpToolNameFlattener.Flatten(command.Path, separator);

			if (promptSources.TryGetValue(promptName, out var existingPath)
				&& !string.Equals(existingPath, command.Path, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException(
					$"MCP prompt name collision: '{promptName}' from routes '{existingPath}' and '{command.Path}'.");
			}

			promptSources[promptName] = command.Path;
			adapter.RegisterRoute(promptName, command);
			prompts[promptName] = new ReplMcpServerPrompt(command, promptName, adapter);
		}

		foreach (var registration in _options.Prompts)
		{
			prompts[registration.Name] = McpServerPrompt.Create(
				registration.Handler,
				new McpServerPromptCreateOptions { Name = registration.Name });
		}

		return [.. prompts.Values];
	}

	private bool IsMcpCandidateBeforeValidation(ReplDocCommand unprojectedCommand)
	{
		var command = McpAutomationProjection.Apply(unprojectedCommand);
		return command is not null
			&& !command.IsHidden
			&& command.Annotations?.AutomationHidden != true
			&& (_options.CommandFilter is not { } filter || filter(command));
	}

	private static bool IsToolCandidate(ReplDocCommand command) =>
		!command.IsHidden
		&& command.Annotations?.AutomationHidden != true;

	private static McpServerPrimitiveCollection<T> ToCollection<T>(IReadOnlyList<T> items)
		where T : IMcpServerPrimitive
	{
		var collection = new McpServerPrimitiveCollection<T>();
		foreach (var item in items)
		{
			collection.Add(item);
		}

		return collection;
	}

	private static McpServerResourceCollection ToResourceCollection(IReadOnlyList<McpServerResource> items)
	{
		var collection = new McpServerResourceCollection();
		foreach (var item in items)
		{
			collection.Add(item);
		}

		return collection;
	}

	internal sealed record McpGeneratedSnapshot(
		McpToolAdapter Adapter,
		List<McpServerTool> Tools,
		List<McpServerResource> Resources,
		List<McpServerPrompt> Prompts);
}
