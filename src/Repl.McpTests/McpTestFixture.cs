using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;

namespace Repl.McpTests;

/// <summary>
/// Connects a Repl app's MCP server to an MCP client via in-process pipes.
/// Disposes cleanly on teardown — no stdio involved.
/// </summary>
internal sealed class McpTestFixture : IAsyncDisposable
{
	private readonly CancellationTokenSource _cts = new();
	private readonly Pipe _clientToServer = new();
	private readonly Pipe _serverToClient = new();
	private readonly Task _serverTask;

	private McpTestFixture(McpClient client, Task serverTask)
	{
		Client = client;
		_serverTask = serverTask;
	}

	public McpClient Client { get; }

	public static async Task<McpTestFixture> CreateAsync(Action<ReplApp> configure)
	{
		var app = ReplApp.Create();
		app.UseMcpServer();
		configure(app);

		var model = app.Core.CreateDocumentationModel();
		var adapter = CreateToolAdapter(app, model);
		var serverOptions = BuildServerOptions(model, adapter);

		var fixture = new McpTestFixture(null!, Task.CompletedTask);
		var serverTransport = new StreamServerTransport(
			fixture._clientToServer.Reader.AsStream(),
			fixture._serverToClient.Writer.AsStream(),
			"test-server");

		var server = McpServer.Create(serverTransport, serverOptions);
		var serverTask = server.RunAsync(fixture._cts.Token);

		var clientTransport = new StreamClientTransport(
			fixture._clientToServer.Writer.AsStream(),
			fixture._serverToClient.Reader.AsStream());
		var client = await McpClient.CreateAsync(clientTransport).ConfigureAwait(false);

		return new McpTestFixture(client, serverTask);
	}

	public async ValueTask DisposeAsync()
	{
		await Client.DisposeAsync().ConfigureAwait(false);
		await _cts.CancelAsync().ConfigureAwait(false);

		await _clientToServer.Writer.CompleteAsync().ConfigureAwait(false);
		await _serverToClient.Writer.CompleteAsync().ConfigureAwait(false);

		try
		{
			await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (TimeoutException)
		{
		}

		_cts.Dispose();
	}

	private static McpToolAdapter CreateToolAdapter(ReplApp app, Documentation.ReplDocumentationModel model)
	{
		var adapter = new McpToolAdapter(app.Core, new ReplMcpServerOptions(), EmptyServiceProvider.Instance);
		foreach (var command in model.Commands)
		{
			if (command.IsHidden || command.Annotations?.AutomationHidden == true)
			{
				continue;
			}

			var toolName = McpToolNameFlattener.Flatten(command.Path, '_');
			adapter.RegisterRoute(toolName, command);
		}

		return adapter;
	}

	private static McpServerOptions BuildServerOptions(
		Documentation.ReplDocumentationModel model,
		McpToolAdapter adapter)
	{
		var tools = new McpServerPrimitiveCollection<McpServerTool>();
		foreach (var command in model.Commands)
		{
			if (command.IsHidden || command.Annotations?.AutomationHidden == true)
			{
				continue;
			}

			var toolName = McpToolNameFlattener.Flatten(command.Path, '_');
			tools.Add(new ReplMcpServerTool(command, toolName, adapter));
		}

		return new McpServerOptions
		{
			ServerInfo = new Implementation { Name = "test-server", Version = "1.0.0" },
			Capabilities = new ServerCapabilities
			{
				Tools = new ToolsCapability { ListChanged = true },
			},
			ToolCollection = tools,
		};
	}

	private sealed class EmptyServiceProvider : IServiceProvider
	{
		public static readonly EmptyServiceProvider Instance = new();
		public object? GetService(Type serviceType) => null;
	}
}
