using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Documentation;
using Repl.Interaction;
using Repl.Internal.Options;

// Roots, Sampling, and Logging are deprecated by MCP spec 2026-07-28 (SEP-2577, SDK
// diagnostic MCP9005) with no replacement API; hosts still rely on them, so Repl keeps
// supporting the features until the SDK removes them. Tracked in issue #51.
#pragma warning disable MCP9005

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
	private readonly McpClientRootsService _roots;
	private readonly McpSamplingService _sampling;
	private readonly McpElicitationService _elicitation;
	private readonly McpFeedbackService _feedback;
	private readonly IServiceProvider _sessionServices;
	private readonly SemaphoreSlim _snapshotGate = new(initialCount: 1, maxCount: 1);
	private readonly Lock _refreshLock = new();
	private readonly Lock _attachLock = new();

	private McpGeneratedSnapshot? _snapshot;
	private SnapshotVersionState _snapshotState = new(Version: 1, LastVisibilityRetractionVersion: 0);
	private long _builtSnapshotVersion;
	private McpServer? _server;
	private EventHandler<RoutingInvalidatedEventArgs>? _routingChangedHandler;
	private ITimer? _debounceTimer;
	private int _rootsNotificationRegistered;
	private int _compatibilityIntroServed;
	private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(100);

	// Notifications are fire-and-forget best-effort — a stuck stdio peer must not hang this
	// indefinitely, since nothing awaits it and it would otherwise pile up one task per
	// invalidation forever.
	private static readonly TimeSpan NotificationSendTimeout = TimeSpan.FromSeconds(5);

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
		_roots = new McpClientRootsService(app);
		_sampling = new McpSamplingService();
		_elicitation = new McpElicitationService();
		_feedback = new McpFeedbackService();
		_sessionServices = new McpServiceProviderOverlay(
			services,
			new Dictionary<Type, object>
			{
				[typeof(IMcpClientRoots)] = _roots,
				[typeof(IMcpSampling)] = _sampling,
				[typeof(IMcpElicitation)] = _elicitation,
				[typeof(IMcpFeedback)] = _feedback,
			});
	}

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
			var server = McpServer.Create(transport, serverOptions, serviceProvider: _sessionServices);
			AttachServer(server);

			try
			{
				await server.RunAsync(ct).ConfigureAwait(false);
			}
			finally
			{
				UnsubscribeFromRoutingChanges();
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
			_ = CreateDocumentationModel();
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
		};
	}

	internal McpServerOptions BuildStaticServerOptions()
	{
		var serverName = _options.ServerName ?? ResolveAppName() ?? "repl-mcp-server";
		var serverVersion = _options.ServerVersion ?? "1.0.0";
		var snapshot = BuildSnapshotCore();

		return new McpServerOptions
		{
			ServerInfo = new Implementation { Name = serverName, Version = serverVersion },
			Capabilities = BuildCapabilities(),
			ToolCollection = ToCollection(snapshot.Tools),
			ResourceCollection = ToResourceCollection(snapshot.Resources),
			PromptCollection = ToCollection(snapshot.Prompts),
		};
	}

	internal McpGeneratedSnapshot BuildSnapshotForTests() => BuildSnapshotCore();

	internal async Task<McpGeneratedSnapshot> BuildSnapshotForTestsAsync(CancellationToken cancellationToken = default) =>
		await GetSnapshotAsync(server: null, cancellationToken).ConfigureAwait(false);

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
		AttachServer(request.Server);
		var snapshot = await GetSnapshotAsync(request.Server, cancellationToken).ConfigureAwait(false);

		if (_options.DynamicToolCompatibility == DynamicToolCompatibilityMode.DiscoverAndCallShim
			&& Interlocked.CompareExchange(ref _compatibilityIntroServed, 1, 0) == 0)
		{
			_ = SendNotificationSafeAsync(NotificationMethods.ToolListChangedNotification);
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
		AttachServer(request.Server);
		var snapshot = await GetSnapshotAsync(request.Server, cancellationToken).ConfigureAwait(false);
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
		AttachServer(request.Server);
		var snapshot = await GetSnapshotAsync(request.Server, cancellationToken).ConfigureAwait(false);
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
		AttachServer(request.Server);
		var snapshot = await GetSnapshotAsync(request.Server, cancellationToken).ConfigureAwait(false);
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
		AttachServer(request.Server);
		var snapshot = await GetSnapshotAsync(request.Server, cancellationToken).ConfigureAwait(false);
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
		AttachServer(request.Server);
		var snapshot = await GetSnapshotAsync(request.Server, cancellationToken).ConfigureAwait(false);
		return new ListPromptsResult
		{
			Prompts = [.. snapshot.Prompts.Select(static prompt => prompt.ProtocolPrompt)],
		};
	}

	private async ValueTask<GetPromptResult> GetPromptAsync(
		RequestContext<GetPromptRequestParams> request,
		CancellationToken cancellationToken)
	{
		AttachServer(request.Server);
		var snapshot = await GetSnapshotAsync(request.Server, cancellationToken).ConfigureAwait(false);
		var promptName = request.Params.Name ?? string.Empty;
		var prompt = snapshot.Prompts.FirstOrDefault(candidate =>
			string.Equals(candidate.ProtocolPrompt.Name, promptName, StringComparison.OrdinalIgnoreCase));
		if (prompt is null)
		{
			throw new McpException($"Unknown prompt: {promptName}");
		}

		return await prompt.GetAsync(request, cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<McpGeneratedSnapshot> GetSnapshotAsync(
		McpServer? server,
		CancellationToken cancellationToken)
	{
		AttachServer(server);

		var snapshotVersion = Volatile.Read(ref _snapshotState).Version;
		if (Volatile.Read(ref _builtSnapshotVersion) == snapshotVersion
			&& _snapshot is { } cached)
		{
			return cached;
		}

		await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			snapshotVersion = Volatile.Read(ref _snapshotState).Version;
			if (Volatile.Read(ref _builtSnapshotVersion) == snapshotVersion
				&& _snapshot is { } refreshed)
			{
				return refreshed;
			}

			var previousSnapshot = _snapshot;
			try
			{
				return await BuildCurrentSnapshotAsync(snapshotVersion, cancellationToken).ConfigureAwait(false);
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
					<= Volatile.Read(ref _builtSnapshotVersion))
			{
				// Preserve availability for transient projection failures, but leave the version dirty
				// so the next request retries without requiring another routing mutation.
				_snapshot = previousSnapshot;
				return previousSnapshot;
			}
		}
		finally
		{
			_snapshotGate.Release();
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
		long snapshotVersion,
		CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await _roots.GetAsync(cancellationToken).ConfigureAwait(false);
			var built = BuildSnapshotCore();
			var observedState = Volatile.Read(ref _snapshotState);

			// Version and retraction watermark are one atomically published state. A reader can
			// therefore never observe the new version without the visibility retraction that caused it.
			// If that state appeared after projection started, discard the result and rebuild.
			if (observedState.LastVisibilityRetractionVersion > snapshotVersion)
			{
				snapshotVersion = observedState.Version;
				continue;
			}

			_snapshot = built;
			if (observedState.Version == snapshotVersion)
			{
				Volatile.Write(ref _builtSnapshotVersion, snapshotVersion);
			}
			return built;
		}
	}

	private McpGeneratedSnapshot BuildSnapshotCore()
	{
		// Project once here so tools/list, tools/call and prompts/list all read the same option list.
		var model = McpAutomationProjection.Apply(CreateDocumentationModel());
		var adapter = new McpToolAdapter(_app, _options, _sessionServices);
		var commandsByPath = model.Commands.ToDictionary(
			command => command.Path,
			command => command,
			StringComparer.OrdinalIgnoreCase);
		var tools = GenerateAllTools(model, adapter, _separator, commandsByPath);
		ValidateCompatibilityToolNames(tools);
		var resources = GenerateResources(model, adapter, _separator, commandsByPath);
		var prompts = CollectPrompts(model, adapter, _separator);
		return new McpGeneratedSnapshot(adapter, tools, resources, prompts);
	}

	private ReplDocumentationModel CreateDocumentationModel()
	{
		var coreApp = _app as CoreReplApp
			?? throw new InvalidOperationException("MCP server handler requires CoreReplApp.");

		var previousProgrammatic = ReplSessionIO.IsProgrammatic;
		ReplSessionIO.IsProgrammatic = true;
		try
		{
			return coreApp.CreateDocumentationModel(CreateDiscoveryServices(), IsMcpCandidateBeforeValidation);
		}
		finally
		{
			ReplSessionIO.IsProgrammatic = previousProgrammatic;
		}
	}

	// Every MCP tool invocation overlays a concrete interaction channel before entering the
	// binder. Discovery must expose that guaranteed fallback even when the caller did not supply
	// a base provider (or supplied one without the channel).
	private McpServiceProviderOverlay CreateDiscoveryServices() =>
		new(
			_sessionServices,
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

	private void AttachServer(McpServer? server)
	{
		if (server is null)
		{
			return;
		}

		lock (_attachLock)
		{
			if (ReferenceEquals(_server, server))
			{
				return;
			}

			_server = server;
			_roots.AttachServer(server);
			_sampling.AttachServer(server);
			_elicitation.AttachServer(server);
			_feedback.AttachServer(server);
			EnsureRoutingSubscription();
			EnsureRootsNotificationHandler(server);
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

	private void EnsureRootsNotificationHandler(McpServer server)
	{
		if (Interlocked.Exchange(ref _rootsNotificationRegistered, 1) != 0)
		{
			return;
		}

		var weakSelf = new WeakReference<McpServerHandler>(this);
		_ = server.RegisterNotificationHandler(
			NotificationMethods.RootsListChangedNotification,
			(_, _) =>
			{
				if (weakSelf.TryGetTarget(out var target))
				{
					target._roots.HandleRootsListChanged();
				}

				return ValueTask.CompletedTask;
			});
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
			Interlocked.Exchange(ref _compatibilityIntroServed, 0);
		}

		lock (_refreshLock)
		{
			_debounceTimer?.Dispose();
			_debounceTimer = _timeProvider.CreateTimer(
				_ => _ = SendDiscoveryNotificationsSafeAsync(),
				state: null,
				dueTime: DebounceDelay,
				period: Timeout.InfiniteTimeSpan);
		}
	}

	private async Task SendDiscoveryNotificationsSafeAsync()
	{
		await SendNotificationSafeAsync(NotificationMethods.ToolListChangedNotification).ConfigureAwait(false);
		await SendNotificationSafeAsync(NotificationMethods.ResourceListChangedNotification).ConfigureAwait(false);
		await SendNotificationSafeAsync(NotificationMethods.PromptListChangedNotification).ConfigureAwait(false);
	}

	private async Task SendNotificationSafeAsync(string method)
	{
		try
		{
			var server = _server;
			if (server is null)
			{
				return;
			}

			using var timeoutCts = new CancellationTokenSource(NotificationSendTimeout);
			await server.SendNotificationAsync(method, timeoutCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Notifications are best-effort. Cancellation is not actionable here.
		}
		catch (Exception)
		{
			// Notifications are best-effort. The next list/read request will rebuild on demand.
		}
	}

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
		var capabilities = new ServerCapabilities
		{
			Logging = new LoggingCapability(),
			Tools = new ToolsCapability { ListChanged = true },
			Resources = new ResourcesCapability { ListChanged = true },
			Prompts = new PromptsCapability { ListChanged = true },
		};

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
			using var runtimeStateScope = coreApp.PushRuntimeState(
				CreateDiscoveryServices(),
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
		Dictionary<string, ReplDocCommand> commandsByPath)
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
			resources.Add(new McpAppResource(uiResource, _sessionServices));
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
