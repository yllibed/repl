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
		var tools = GenerateTools(model, adapter, separator);

		var serverName = _options.ServerName
			?? model.App.Name
			?? "repl-mcp-server";
		var serverVersion = _options.ServerVersion ?? "1.0.0";

		var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
		foreach (var tool in tools)
		{
			toolCollection.Add(tool);
		}

		// Use StdioServerTransport for stdio-based MCP server.
		// The IReplIoContext streams are passthrough (stdin/stdout) when AsProtocolPassthrough is active.
		_ = io; // io.Input/Output are TextReader/TextWriter, SDK needs raw streams via StdioServerTransport.
		var transport = new StdioServerTransport(serverName);
		try
		{
			var server = McpServer.Create(transport, new McpServerOptions
			{
				ServerInfo = new Implementation
				{
					Name = serverName,
					Version = serverVersion,
				},
				Capabilities = new ServerCapabilities
				{
					Tools = new ToolsCapability { ListChanged = true },
				},
				ToolCollection = toolCollection,
			}, serviceProvider: _services);
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

	private List<McpServerTool> GenerateTools(
		ReplDocumentationModel model,
		McpToolAdapter adapter,
		char separator)
	{
		var tools = new List<McpServerTool>();
		var nameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var command in model.Commands)
		{
			if (command.IsHidden)
			{
				continue;
			}

			if (command.Annotations?.AutomationHidden == true)
			{
				continue;
			}

			if (_options.CommandFilter is { } filter && !filter(command))
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
}
