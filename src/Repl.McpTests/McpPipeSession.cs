using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Repl.McpTests;

/// <summary>
/// One MCP client connected to an existing <see cref="Repl.Mcp.McpServerHandler"/> over in-process pipes.
/// </summary>
/// <remarks>
/// Several sessions can share one handler, which is what the concurrent-session regressions need.
/// This type owns the transport pair, the cancellation source, and — crucially — the
/// <c>RunAsync</c> task: <see cref="DisposeAsync"/> awaits it and lets any fault surface. Discarding
/// that task is what once let a server throwing during teardown leave every concurrency test green.
/// </remarks>
internal sealed class McpPipeSession : IAsyncDisposable
{
	private readonly CancellationTokenSource _cts;
	private readonly Pipe _clientToServer;
	private readonly Pipe _serverToClient;
	private readonly Task _serverTask;

	private McpPipeSession(
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

	/// <summary>
	/// Starts a session by handing <paramref name="startServer"/> the server side of a fresh pipe pair.
	/// </summary>
	/// <remarks>
	/// The handshake races the server task so a server that fails while starting surfaces its own
	/// exception instead of an initialize timeout carrying the wrong one.
	/// </remarks>
	public static async Task<McpPipeSession> StartAsync(
		Func<McpTestFixture.PipeIoContext, CancellationToken, Task> startServer,
		McpClientOptions? clientOptions,
		CancellationToken cancellationToken)
	{
		var clientToServer = new Pipe();
		var serverToClient = new Pipe();
		var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		var io = new McpTestFixture.PipeIoContext(
			clientToServer.Reader.AsStream(),
			serverToClient.Writer.AsStream());
		var serverTask = startServer(io, cts.Token);

		var clientTransport = new StreamClientTransport(
			clientToServer.Writer.AsStream(),
			serverToClient.Reader.AsStream());

		try
		{
			var clientTask = McpClient.CreateAsync(clientTransport, clientOptions, cancellationToken: cts.Token);
			if (ReferenceEquals(await Task.WhenAny(serverTask, clientTask).ConfigureAwait(false), serverTask))
			{
				// Rethrows a start failure; a clean early exit means the handshake never completes.
				await serverTask.ConfigureAwait(false);

				throw new InvalidOperationException(
					"The MCP server stopped before the client completed its handshake.");
			}

			var client = await clientTask.ConfigureAwait(false);

			return new McpPipeSession(client, serverTask, cts, clientToServer, serverToClient);
		}
		catch
		{
			await cts.CancelAsync().ConfigureAwait(false);
			await clientToServer.Writer.CompleteAsync().ConfigureAwait(false);
			await serverToClient.Writer.CompleteAsync().ConfigureAwait(false);
			cts.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Closes the session and asserts the server terminated cleanly.
	/// </summary>
	/// <remarks>
	/// Only cancellation is an accepted outcome. A fault propagates, and so does a timeout: a server
	/// that never shuts down is a defect, not noise to be swallowed.
	/// </remarks>
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
			// Expected: the server's RunAsync ends on cancellation.
		}
		finally
		{
			_cts.Dispose();
		}
	}
}
