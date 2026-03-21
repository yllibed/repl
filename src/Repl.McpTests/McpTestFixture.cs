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
	private readonly ReplApp _app;

	private McpTestFixture(
		ReplApp app,
		McpClient client,
		Task serverTask,
		CancellationTokenSource cts,
		Pipe clientToServer,
		Pipe serverToClient)
	{
		_app = app;
		Client = client;
		_serverTask = serverTask;
		_cts = cts;
		_clientToServer = clientToServer;
		_serverToClient = serverToClient;
	}

	public ReplApp App => _app;
	public McpClient Client { get; }

	public static Task<McpTestFixture> CreateAsync(Action<ReplApp> configure) =>
		CreateAsync(configure, configureOptions: null, clientOptions: null);

	public static async Task<McpTestFixture> CreateAsync(
		Action<ReplApp> configure,
		Action<ReplMcpServerOptions>? configureOptions,
		McpClientOptions? clientOptions = null)
	{
		var app = ReplApp.Create();
		app.UseMcpServer(configureOptions);
		configure(app);

		var options = new ReplMcpServerOptions();
		configureOptions?.Invoke(options);

		var handler = new McpServerHandler(app.Core, options, EmptyServiceProvider.Instance);
		var serverOptions = handler.BuildServerOptions();

		var clientToServer = new Pipe();
		var serverToClient = new Pipe();
		var cts = new CancellationTokenSource();

		var serverName = serverOptions.ServerInfo?.Name ?? "test-server";
		var inputStream = clientToServer.Reader.AsStream();
		var outputStream = serverToClient.Writer.AsStream();
		var ioContext = new PipeIoContext(inputStream, outputStream);
		ITransport serverTransport = options.TransportFactory is { } factory
			? factory(serverName, ioContext)
			: new StreamServerTransport(inputStream, outputStream, serverName);

		var server = McpServer.Create(serverTransport, serverOptions);
		var serverTask = server.RunAsync(cts.Token);

		var clientTransport = new StreamClientTransport(
			clientToServer.Writer.AsStream(),
			serverToClient.Reader.AsStream());
		var client = await McpClient.CreateAsync(clientTransport, clientOptions).ConfigureAwait(false);

		return new McpTestFixture(app, client, serverTask, cts, clientToServer, serverToClient);
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

	internal sealed class PipeIoContext(Stream inputStream, Stream outputStream) : IReplIoContext
	{
		public Stream InputStream => inputStream;
		public Stream OutputStream => outputStream;
		public TextReader Input => new StreamReader(inputStream);
		public TextWriter Output => new StreamWriter(outputStream);
		public TextWriter Error => TextWriter.Null;
		public bool IsHostedSession => false;
		public string? SessionId => null;
	}
}
