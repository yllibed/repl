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
	private readonly McpPipeSession _session;
	private readonly ReplApp _app;

	private McpTestFixture(ReplApp app, McpPipeSession session)
	{
		_app = app;
		_session = session;
	}

	public ReplApp App => _app;
	public McpClient Client => _session.Client;

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
		options.TransportFactory ??= PipeTransportFactory;

		var handler = new McpServerHandler(app.Core, options, app.Services);

		var session = await McpPipeSession
			.StartAsync(handler.RunAsync, clientOptions, CancellationToken.None)
			.ConfigureAwait(false);

		return new McpTestFixture(app, session);
	}

	/// <summary>Builds the server transport over the pipe pair a <see cref="McpPipeSession"/> supplies.</summary>
	internal static Func<string, IReplIoContext, ITransport> PipeTransportFactory { get; } =
		static (serverName, io) => new StreamServerTransport(
			((PipeIoContext)io).InputStream,
			((PipeIoContext)io).OutputStream,
			serverName);

	public ValueTask DisposeAsync() => _session.DisposeAsync();

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
