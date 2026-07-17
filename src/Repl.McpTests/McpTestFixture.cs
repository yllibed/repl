using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
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

	public static Task<McpTestFixture> CreateAsync(
		Action<ReplApp> configure,
		Action<IServiceCollection>? configureServices) =>
		CreateAsync(configure, configureOptions: null, clientOptions: null, configureServices: configureServices);

	public static async Task<McpTestFixture> CreateAsync(
		Action<ReplApp> configure,
		Action<ReplMcpServerOptions>? configureOptions,
		McpClientOptions? clientOptions = null,
		Action<IServiceCollection>? configureServices = null)
	{
		var app = configureServices is not null
			? ReplApp.Create(configureServices)
			: ReplApp.Create();
		app.UseMcpServer(configureOptions);
		configure(app);

		var options = new ReplMcpServerOptions();
		configureOptions?.Invoke(options);

		var serviceProvider = app.Services;
		var handler = new McpServerHandler(app.Core, options, serviceProvider);

		var clientToServer = new Pipe();
		var serverToClient = new Pipe();
		var cts = new CancellationTokenSource();

		var inputStream = clientToServer.Reader.AsStream();
		var outputStream = serverToClient.Writer.AsStream();
		var ioContext = new PipeIoContext(inputStream, outputStream);
		if (options.TransportFactory is null)
		{
			options.TransportFactory = static (serverName, io) => new StreamServerTransport(
				((PipeIoContext)io).InputStream,
				((PipeIoContext)io).OutputStream,
				serverName);
		}
		var serverTask = handler.RunAsync(ioContext, cts.Token);

		var clientTransport = new StreamClientTransport(
			clientToServer.Writer.AsStream(),
			serverToClient.Reader.AsStream());

		try
		{
			// Race the handshake against the server. Awaiting only the client means a server that
			// fails while starting is observable solely as an initialize timeout carrying the wrong
			// exception — which is what once pushed production code into throwing synchronously
			// just to stay testable.
			var clientTask = McpClient.CreateAsync(clientTransport, clientOptions);
			if (ReferenceEquals(await Task.WhenAny(serverTask, clientTask).ConfigureAwait(false), serverTask))
			{
				// Rethrows a start failure; a clean early exit means the handshake never completes.
				await serverTask.ConfigureAwait(false);

				throw new InvalidOperationException(
					"The MCP server stopped before the client completed its handshake.");
			}

			var client = await clientTask.ConfigureAwait(false);

			return new McpTestFixture(app, client, serverTask, cts, clientToServer, serverToClient);
		}
		catch
		{
			await AbandonAsync(cts, clientToServer, serverToClient).ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>
	/// Releases what <see cref="CreateAsync(Action{ReplApp}, Action{ReplMcpServerOptions}, McpClientOptions, Action{IServiceCollection})"/>
	/// allocated when construction fails before the fixture takes ownership.
	/// </summary>
	private static async Task AbandonAsync(
		CancellationTokenSource cts,
		Pipe clientToServer,
		Pipe serverToClient)
	{
		await cts.CancelAsync().ConfigureAwait(false);
		await clientToServer.Writer.CompleteAsync().ConfigureAwait(false);
		await serverToClient.Writer.CompleteAsync().ConfigureAwait(false);
		cts.Dispose();
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

	internal static IServiceProvider EmptyServices => EmptyServiceProvider.Instance;

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
