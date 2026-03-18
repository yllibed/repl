using System.Diagnostics.CodeAnalysis;
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
	private readonly ICoreReplApp _app;
	private readonly ReplMcpServerOptions _options;
	private readonly IServiceProvider _services;

	public McpServerHandler(
		ICoreReplApp app,
		ReplMcpServerOptions options,
		IServiceProvider services)
	{
		_app = app;
		_options = options;
		_services = services;
	}

	[UnconditionalSuppressMessage(
		"Trimming",
		"IL2026",
		Justification = "MCP server handler runs in a context where all types are preserved.")]
	public async Task RunAsync(IReplIoContext io, CancellationToken ct)
	{
		// Build the doc model with Programmatic channel so module presence
		// predicates see the same channel as tool dispatch.
		ReplSessionIO.IsProgrammatic = true;
		var model = _app.CreateDocumentationModel();
		var adapter = new McpToolAdapter(_app, _options, _services);
		var separator = McpToolNameFlattener.ResolveSeparator(_options.ToolNamingSeparator);
		var serverOptions = BuildServerOptions(model, adapter, separator);

		_ = io; // AsProtocolPassthrough reserves stdin/stdout for the transport.
		var serverName = serverOptions.ServerInfo?.Name ?? "repl-mcp-server";
		var transport = new StdioServerTransport(serverName);
		try
		{
			var server = McpServer.Create(transport, serverOptions, serviceProvider: _services);
			try
			{
				SubscribeToRoutingChanges(
					adapter, separator,
					serverOptions.ToolCollection!,
					serverOptions.ResourceCollection!,
					serverOptions.PromptCollection!);
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

	private McpServerOptions BuildServerOptions(
		ReplDocumentationModel model,
		McpToolAdapter adapter,
		char separator)
	{
		var tools = GenerateAllTools(model, adapter, separator);
		var resources = GenerateResources(model, adapter, separator);
		var prompts = CollectPrompts(model, adapter, separator);

		var serverName = _options.ServerName ?? model.App.Name ?? "repl-mcp-server";
		var serverVersion = _options.ServerVersion ?? "1.0.0";

		return new McpServerOptions
		{
			ServerInfo = new Implementation { Name = serverName, Version = serverVersion },
			Capabilities = BuildCapabilities(resources, prompts),
			ToolCollection = ToCollection(tools),
			ResourceCollection = ToResourceCollection(resources),
			PromptCollection = ToCollection(prompts),
		};
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
		char separator)
	{
		var tools = new List<McpServerTool>();
		var nameSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		// 1. Regular commands. Resource-only and prompt-only commands are handled
		//    separately as fallback tools (opt-in). ReadOnly+AsResource commands
		//    are regular tools (they have a behavioral annotation, not just a marker).
		foreach (var command in model.Commands)
		{
			if (!IsToolCandidate(command))
			{
				continue;
			}

			if (command.IsPrompt)
			{
				continue; // Handled in phase 3 (prompt fallback).
			}

			if (command.IsResource && command.Annotations?.ReadOnly != true)
			{
				continue; // Resource-only (no ReadOnly annotation) — handled in phase 2.
			}

			AddTool(command, tools, nameSet, adapter, separator);
		}

		// 2. Resource fallback: resource-only commands as tools.
		if (_options.ResourceFallbackToTools)
		{
			foreach (var resource in model.Resources)
			{
				var docCommand = model.Commands.FirstOrDefault(c =>
					string.Equals(c.Path, resource.Path, StringComparison.OrdinalIgnoreCase));
				if (docCommand is not null && IsToolCandidate(docCommand))
				{
					AddTool(docCommand, tools, nameSet, adapter, separator);
				}
			}
		}

		// 3. Prompt fallback: prompt commands as tools.
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
			// Same command registered from multiple phases (e.g. ReadOnly is both
			// a core tool and a resource fallback) — skip silently.
			if (string.Equals(command.Path, existingPath, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			// Different routes collapsed to the same name — surface at startup.
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
		char separator)
	{
		var resources = new List<McpServerResource>();

		foreach (var resource in model.Resources)
		{
			var docCommand = model.Commands.FirstOrDefault(c =>
				string.Equals(c.Path, resource.Path, StringComparison.OrdinalIgnoreCase));

			// Hidden and AutomationHidden commands are excluded from all MCP surfaces.
			if (docCommand is not null && !IsToolCandidate(docCommand))
			{
				continue;
			}

			// Skip auto-promoted ReadOnly resources when opt-out is active.
			if (!_options.AutoPromoteReadOnlyToResources
				&& docCommand is not null
				&& !docCommand.IsResource
				&& docCommand.Annotations?.ReadOnly == true)
			{
				continue;
			}
			var resourceName = McpToolNameFlattener.Flatten(resource.Path, separator);
			var resourceUri = McpToolNameFlattener.Flatten(resource.Path, '/');
			var uriTemplate = $"repl://{resourceUri}";

			var mcpResource = McpServerResource.Create(
				async (CancellationToken ct) =>
				{
					var result = await adapter.InvokeAsync(
						resourceName,
						new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal),
						server: null, progressToken: null, ct).ConfigureAwait(false);
					return result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
				},
				new McpServerResourceCreateOptions
				{
					Name = resourceName,
					Description = resource.Description,
					UriTemplate = uriTemplate,
				});

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

		foreach (var command in model.Commands)
		{
			if (!command.IsPrompt || !IsToolCandidate(command))
			{
				continue;
			}

			var promptName = McpToolNameFlattener.Flatten(command.Path, separator);
			adapter.RegisterRoute(promptName, command);
			prompts[promptName] = CreatePipelinePrompt(promptName, command, adapter);
		}

		foreach (var registration in _options.Prompts)
		{
			prompts[registration.Name] = McpServerPrompt.Create(
				registration.Handler,
				new McpServerPromptCreateOptions { Name = registration.Name });
		}

		return [.. prompts.Values];
	}

	private static McpServerPrompt CreatePipelinePrompt(
		string promptName,
		ReplDocCommand command,
		McpToolAdapter adapter) =>
		McpServerPrompt.Create(
			async (IDictionary<string, string?>? arguments, CancellationToken ct) =>
			{
				var jsonArgs = ConvertPromptArguments(arguments);
				var result = await adapter.InvokeAsync(
					promptName, jsonArgs, server: null, progressToken: null, ct)
					.ConfigureAwait(false);

				var text = result.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text
					?? command.Description ?? promptName;

				return new GetPromptResult
				{
					Messages =
					[
						new PromptMessage
						{
							Role = Role.User,
							Content = new TextContentBlock { Text = text },
						},
					],
				};
			},
			new McpServerPromptCreateOptions { Name = promptName, Description = command.Description });

	private static Dictionary<string, System.Text.Json.JsonElement> ConvertPromptArguments(
		IDictionary<string, string?>? arguments)
	{
		var jsonArgs = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
		if (arguments is null)
		{
			return jsonArgs;
		}

		foreach (var (key, value) in arguments)
		{
			jsonArgs[key] = System.Text.Json.JsonSerializer.SerializeToElement(
				value, McpJsonContext.Default.String);
		}

		return jsonArgs;
	}

	// ── Helpers ─────────────────────────────────────────────────────────

	private bool IsToolCandidate(ReplDocCommand command) =>
		!command.IsHidden
		&& command.Annotations?.AutomationHidden != true
		&& (_options.CommandFilter is not { } filter || filter(command));

	private static ServerCapabilities BuildCapabilities(
		List<McpServerResource> resources,
		List<McpServerPrompt> prompts)
	{
		var capabilities = new ServerCapabilities
		{
			Tools = new ToolsCapability { ListChanged = true },
		};

		if (resources.Count > 0)
		{
			capabilities.Resources = new ResourcesCapability { ListChanged = true };
		}

		if (prompts.Count > 0)
		{
			capabilities.Prompts = new PromptsCapability { ListChanged = true };
		}

		return capabilities;
	}

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

	// ── list_changed on routing invalidation ───────────────────────────

	private EventHandler? _routingChangedHandler;

	private void SubscribeToRoutingChanges(
		McpToolAdapter adapter,
		char separator,
		McpServerPrimitiveCollection<McpServerTool> toolCollection,
		McpServerResourceCollection resourceCollection,
		McpServerPrimitiveCollection<McpServerPrompt> promptCollection)
	{
		if (_app is not CoreReplApp coreApp)
		{
			return;
		}

		_routingChangedHandler = (_, _) =>
		{
			// Resolve doc model with Programmatic channel to match tool dispatch context.
			var previousProgrammatic = ReplSessionIO.IsProgrammatic;
			ReplSessionIO.IsProgrammatic = true;
			try
			{
				var newModel = _app.CreateDocumentationModel();
				RefreshCollection(toolCollection, GenerateAllTools(newModel, adapter, separator));
				RefreshCollection(resourceCollection, GenerateResources(newModel, adapter, separator));
				RefreshCollection(promptCollection, CollectPrompts(newModel, adapter, separator));
			}
			finally
			{
				ReplSessionIO.IsProgrammatic = previousProgrammatic;
			}
		};

		coreApp.RoutingInvalidated += _routingChangedHandler;
	}

	private void UnsubscribeFromRoutingChanges()
	{
		if (_routingChangedHandler is not null && _app is CoreReplApp coreApp)
		{
			coreApp.RoutingInvalidated -= _routingChangedHandler;
			_routingChangedHandler = null;
		}
	}

	private static void RefreshCollection<T>(McpServerPrimitiveCollection<T> collection, IReadOnlyList<T> items)
		where T : IMcpServerPrimitive
	{
		collection.Clear();
		foreach (var item in items)
		{
			collection.Add(item);
		}
	}

	private static void RefreshCollection(McpServerResourceCollection collection, IReadOnlyList<McpServerResource> items)
	{
		collection.Clear();
		foreach (var item in items)
		{
			collection.Add(item);
		}
	}
}
