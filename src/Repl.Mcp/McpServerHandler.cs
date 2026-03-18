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
		var model = _app.CreateDocumentationModel();
		var adapter = new McpToolAdapter(_app, _options, _services);
		var separator = McpToolNameFlattener.ResolveSeparator(_options.ToolNamingSeparator);
		var serverOptions = BuildServerOptions(model, adapter, separator);

		// AsProtocolPassthrough reserves stdin/stdout for the transport.
		_ = io;
		var serverName = serverOptions.ServerInfo?.Name ?? "repl-mcp-server";
		var transport = new StdioServerTransport(serverName);
		try
		{
			var server = McpServer.Create(transport, serverOptions, serviceProvider: _services);
			try
			{
				await server.RunAsync(ct).ConfigureAwait(false);
			}
			finally
			{
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
		var tools = GenerateTools(model, adapter, separator);
		var resources = GenerateResources(model, adapter, separator);
		var prompts = CollectPrompts(model, separator);

		var serverName = _options.ServerName ?? model.App.Name ?? "repl-mcp-server";
		var serverVersion = _options.ServerVersion ?? "1.0.0";

		var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
		foreach (var tool in tools)
		{
			toolCollection.Add(tool);
		}

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

		return new McpServerOptions
		{
			ServerInfo = new Implementation { Name = serverName, Version = serverVersion },
			Capabilities = capabilities,
			ToolCollection = toolCollection,
		};
	}

	private List<McpServerTool> GenerateTools(
		ReplDocumentationModel model,
		McpToolAdapter adapter,
		char separator)
	{
		var tools = new List<McpServerTool>();
		var nameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var command in model.Commands)
		{
			if (!IsToolCandidate(command))
			{
				continue;
			}

			var toolName = McpToolNameFlattener.Flatten(command.Path, separator);
			if (!nameSet.Add(toolName))
			{
				throw new InvalidOperationException(
					$"MCP tool name collision: '{toolName}' from route '{command.Path}'. " +
					"Consider a different ToolNamingSeparator or rename one of the commands.");
			}

			adapter.RegisterRoute(toolName, command);
			tools.Add(new ReplMcpServerTool(command, toolName, adapter));
		}

		return tools;
	}

	private static List<McpServerResource> GenerateResources(
		ReplDocumentationModel model,
		McpToolAdapter adapter,
		char separator)
	{
		var resources = new List<McpServerResource>();

		foreach (var resource in model.Resources)
		{
			var resourceName = McpToolNameFlattener.Flatten(resource.Path, separator);
			var uriTemplate = $"repl://{resourceName}";

			// Create a resource that dispatches through the tool adapter.
			var capturedPath = resource.Path;
			var mcpResource = McpServerResource.Create(
				async (CancellationToken ct) =>
				{
					// Resources dispatch through the same pipeline as tools.
					var result = await adapter.InvokeAsync(
						resourceName,
						new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal),
						server: null!,
						ct).ConfigureAwait(false);

					var text = result.Content?.FirstOrDefault() is TextContentBlock block
						? block.Text ?? ""
						: "";
					return text;
				},
				new McpServerResourceCreateOptions
				{
					Name = resourceName,
					Description = resource.Description,
					UriTemplate = uriTemplate,
				});

			// Ensure the resource route is registered.
			var docCommand = model.Commands.FirstOrDefault(c =>
				string.Equals(c.Path, capturedPath, StringComparison.OrdinalIgnoreCase));
			if (docCommand is not null)
			{
				adapter.RegisterRoute(resourceName, docCommand);
			}

			resources.Add(mcpResource);
		}

		return resources;
	}

	private List<McpServerPrompt> CollectPrompts(
		ReplDocumentationModel model,
		char separator)
	{
		var prompts = new Dictionary<string, McpServerPrompt>(StringComparer.OrdinalIgnoreCase);

		// 1. Prompts from AsPrompt() commands.
		foreach (var command in model.Commands)
		{
			if (!command.IsPrompt)
			{
				continue;
			}

			var promptName = McpToolNameFlattener.Flatten(command.Path, separator);
			var description = command.Description ?? promptName;
			var prompt = McpServerPrompt.Create(
				(string? args) => new GetPromptResult
				{
					Messages =
					[
						new PromptMessage
						{
							Role = Role.User,
							Content = new TextContentBlock { Text = description },
						},
					],
				},
				new McpServerPromptCreateOptions { Name = promptName, Description = command.Description });

			prompts[promptName] = prompt;
		}

		// 2. Explicit registrations via options.Prompt() — override on collision.
		foreach (var registration in _options.Prompts)
		{
			var prompt = McpServerPrompt.Create(
				registration.Handler,
				new McpServerPromptCreateOptions { Name = registration.Name });

			prompts[registration.Name] = prompt;
		}

		return [.. prompts.Values];
	}

	private bool IsToolCandidate(ReplDocCommand command) =>
		!command.IsHidden
		&& command.Annotations?.AutomationHidden != true
		&& (_options.CommandFilter is not { } filter || filter(command));
}
