using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Documentation;

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
	private readonly IServiceProvider _sessionServices;
	private readonly SemaphoreSlim _snapshotGate = new(initialCount: 1, maxCount: 1);
	private readonly Lock _refreshLock = new();
	private readonly Lock _attachLock = new();

	private McpGeneratedSnapshot? _snapshot;
	private long _snapshotVersion = 1;
	private long _builtSnapshotVersion;
	private McpServer? _server;
	private EventHandler? _routingChangedHandler;
	private ITimer? _debounceTimer;
	private int _rootsNotificationRegistered;
	private int _compatibilityIntroServed;
	private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(100);

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
		_sessionServices = new McpServiceProviderOverlay(
			services,
			new Dictionary<Type, object>
			{
				[typeof(IMcpClientRoots)] = _roots,
			});
	}

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2026",
		Justification = "MCP server handler runs in a context where all types are preserved.")]
	public async Task RunAsync(IReplIoContext io, CancellationToken ct)
	{
		var serverOptions = BuildServerOptions();
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

	internal McpServerOptions BuildServerOptions()
	{
		var serverName = _options.ServerName ?? ResolveAppName() ?? "repl-mcp-server";
		var serverVersion = _options.ServerVersion ?? "1.0.0";

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

	internal McpGeneratedSnapshot BuildSnapshotForTests() => BuildSnapshotCore();

	internal async Task<McpGeneratedSnapshot> BuildSnapshotForTestsAsync(CancellationToken cancellationToken = default) =>
		await GetSnapshotAsync(server: null, cancellationToken).ConfigureAwait(false);

	private string? ResolveAppName()
	{
		var previousProgrammatic = ReplSessionIO.IsProgrammatic;
		ReplSessionIO.IsProgrammatic = true;
		try
		{
			var model = CreateDocumentationModel();
			return model.App.Name;
		}
		finally
		{
			ReplSessionIO.IsProgrammatic = previousProgrammatic;
		}
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
		IDictionary<string, JsonElement> arguments = request.Params?.Arguments ?? EmptyArguments;
		var toolName = request.Params?.Name ?? string.Empty;
		var progressToken = request.Params?.ProgressToken;

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
		var uri = request.Params?.Uri ?? string.Empty;
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
		var promptName = request.Params?.Name ?? string.Empty;
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

		var snapshotVersion = Volatile.Read(ref _snapshotVersion);
		if (Volatile.Read(ref _builtSnapshotVersion) == snapshotVersion
			&& _snapshot is { } cached)
		{
			return cached;
		}

		await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			snapshotVersion = Volatile.Read(ref _snapshotVersion);
			if (Volatile.Read(ref _builtSnapshotVersion) == snapshotVersion
				&& _snapshot is { } refreshed)
			{
				return refreshed;
			}

			var previousSnapshot = _snapshot;
			try
			{
				await _roots.GetAsync(cancellationToken).ConfigureAwait(false);
				var built = BuildSnapshotCore();
				_snapshot = built;
				if (Volatile.Read(ref _snapshotVersion) == snapshotVersion)
				{
					Volatile.Write(ref _builtSnapshotVersion, snapshotVersion);
				}
				return built;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception) when (previousSnapshot is not null)
			{
				_snapshot = previousSnapshot;
				if (Volatile.Read(ref _snapshotVersion) == snapshotVersion)
				{
					Volatile.Write(ref _builtSnapshotVersion, snapshotVersion);
				}
				return previousSnapshot;
			}
		}
		finally
		{
			_snapshotGate.Release();
		}
	}

	private McpGeneratedSnapshot BuildSnapshotCore()
	{
		var model = CreateDocumentationModel();
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
			return coreApp.CreateDocumentationModel(_sessionServices);
		}
		finally
		{
			ReplSessionIO.IsProgrammatic = previousProgrammatic;
		}
	}

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
			EnsureRoutingSubscription();
			EnsureRootsNotificationHandler(server);
		}
	}

	private void EnsureRoutingSubscription()
	{
		if (_routingChangedHandler is not null || _app is not CoreReplApp coreApp)
		{
			return;
		}

		var weakSelf = new WeakReference<McpServerHandler>(this);
		EventHandler? handler = null;
		handler = (_, _) =>
		{
			if (!weakSelf.TryGetTarget(out var target))
			{
				coreApp.RoutingInvalidated -= handler;
				return;
			}

			target.OnRoutingInvalidated();
		};

		_routingChangedHandler = handler;
		coreApp.RoutingInvalidated += handler;
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

	private void OnRoutingInvalidated()
	{
		Interlocked.Increment(ref _snapshotVersion);
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

			await server.SendNotificationAsync(method, CancellationToken.None).ConfigureAwait(false);
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
			coreApp.RoutingInvalidated -= _routingChangedHandler;
			_routingChangedHandler = null;
		}

		lock (_refreshLock)
		{
			_debounceTimer?.Dispose();
			_debounceTimer = null;
		}
	}

	private static ServerCapabilities BuildCapabilities() => new()
	{
		Tools = new ToolsCapability { ListChanged = true },
		Resources = new ResourcesCapability { ListChanged = true },
		Prompts = new PromptsCapability { ListChanged = true },
	};

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
		var toolName = McpToolNameFlattener.Flatten(command.Path, separator);
		if (nameSet.TryGetValue(toolName, out var existingPath))
		{
			if (string.Equals(command.Path, existingPath, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			throw new InvalidOperationException(
				$"MCP tool name collision: '{toolName}' from routes '{existingPath}' and '{command.Path}'. " +
				"Consider a different ToolNamingSeparator or rename one of the commands.");
		}

		nameSet[toolName] = command.Path;
		adapter.RegisterRoute(toolName, command);
		tools.Add(new ReplMcpServerTool(command, toolName, adapter));
	}

	// ── Resource generation ────────────────────────────────────────────

	private List<McpServerResource> GenerateResources(
		ReplDocumentationModel model,
		McpToolAdapter adapter,
		char separator,
		Dictionary<string, ReplDocCommand> commandsByPath)
	{
		var resources = new List<McpServerResource>();

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
			var uriTemplate = McpToolNameFlattener.BuildResourceUri(resource.Path, _options.ResourceUriScheme);
			var mcpResource = new ReplMcpServerResource(resource, resourceName, uriTemplate, adapter);

			if (docCommand is not null)
			{
				adapter.RegisterRoute(resourceName, docCommand);
			}

			resources.Add(mcpResource);
		}

		return resources;
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

	private bool IsToolCandidate(ReplDocCommand command) =>
		!command.IsHidden
		&& command.Annotations?.AutomationHidden != true
		&& (_options.CommandFilter is not { } filter || filter(command));

	internal sealed record McpGeneratedSnapshot(
		McpToolAdapter Adapter,
		List<McpServerTool> Tools,
		List<McpServerResource> Resources,
		List<McpServerPrompt> Prompts);
}
