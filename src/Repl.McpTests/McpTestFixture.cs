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
	private readonly CancellationTokenSource _cts;
	private readonly Pipe _clientToServer;
	private readonly Pipe _serverToClient;
	private readonly Task _serverTask;

	private McpTestFixture(
		McpClient client,
		Task serverTask,
		CancellationTokenSource cts,
		Pipe clientToServer,
		Pipe serverToClient)
	{
		Client = client;
		_serverTask = serverTask;
		_cts = cts;
		_clientToServer = clientToServer;
		_serverToClient = serverToClient;
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

		var clientToServer = new Pipe();
		var serverToClient = new Pipe();
		var cts = new CancellationTokenSource();

		var serverTransport = new StreamServerTransport(
			clientToServer.Reader.AsStream(),
			serverToClient.Writer.AsStream(),
			"test-server");

		var server = McpServer.Create(serverTransport, serverOptions);
		var serverTask = server.RunAsync(cts.Token);

		var clientTransport = new StreamClientTransport(
			clientToServer.Writer.AsStream(),
			serverToClient.Reader.AsStream());
		var client = await McpClient.CreateAsync(clientTransport).ConfigureAwait(false);

		return new McpTestFixture(client, serverTask, cts, clientToServer, serverToClient);
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
			// Expected: server RunAsync cancelled during shutdown.
		}
		catch (TimeoutException)
		{
			// Server did not shut down within timeout — transport will be collected.
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
