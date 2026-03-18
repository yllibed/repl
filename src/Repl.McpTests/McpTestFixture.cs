using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Repl.Mcp;

namespace Repl.McpTests;

/// <summary>
/// Connects a Repl app's MCP server to an MCP client via in-process pipes.
/// Uses the real <see cref="McpServerHandler"/> pipeline so fallback options,
/// filtering, and collision detection are exercised end-to-end.
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

	public static Task<McpTestFixture> CreateAsync(Action<ReplApp> configure) =>
		CreateAsync(configure, configureOptions: null);

	public static async Task<McpTestFixture> CreateAsync(
		Action<ReplApp> configure,
		Action<ReplMcpServerOptions>? configureOptions)
	{
		var app = ReplApp.Create();
		app.UseMcpServer(configureOptions);
		configure(app);

		var options = new ReplMcpServerOptions();
		configureOptions?.Invoke(options);

		// Use the real McpServerHandler to build server options — exercises
		// the full tool/resource/prompt generation pipeline with fallbacks.
		var model = app.Core.CreateDocumentationModel();
		var adapter = new McpToolAdapter(app.Core, options, EmptyServiceProvider.Instance);
		var separator = McpToolNameFlattener.ResolveSeparator(options.ToolNamingSeparator);
		var handler = new McpServerHandler(app.Core, options, EmptyServiceProvider.Instance);
		var serverOptions = handler.BuildServerOptions(model, adapter, separator);

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

	private sealed class EmptyServiceProvider : IServiceProvider
	{
		public static readonly EmptyServiceProvider Instance = new();
		public object? GetService(Type serviceType) => null;
	}
}
