using System.Net.WebSockets;
using System.Text;

namespace Repl.WebSocket;

/// <summary>
/// Runs a REPL session over a raw WebSocket connection.
/// </summary>
public static class ReplWebSocketSession
{
	// How long a peer has to answer the session's close before its connection is aborted; the change note in
	// docs/execution-pipeline.md states it too.
	private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);
	private const int MaximumMessageBytes = 64 * 1024;

	/// <summary>
	/// Runs a REPL app session over a WebSocket-backed host.
	/// </summary>
	/// <param name="app">Configured REPL app instance.</param>
	/// <param name="socket">Connected WebSocket.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Execution exit code.</returns>
	/// <inheritdoc cref="StreamedReplHost.RunSessionAsync(ReplApp, ReplRunOptions?, CancellationToken)" path="/exception"/>
	public static ValueTask<int> RunAsync(
		ReplApp app,
		System.Net.WebSockets.WebSocket socket,
		CancellationToken cancellationToken = default) =>
		RunAsync(app, socket, options: null, cancellationToken);

	/// <summary>
	/// Runs a REPL app session over a WebSocket-backed host using explicit run options.
	/// </summary>
	/// <param name="app">Configured REPL app instance.</param>
	/// <param name="socket">Connected WebSocket.</param>
	/// <param name="options">Run options.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Execution exit code.</returns>
	/// <inheritdoc cref="StreamedReplHost.RunSessionAsync(ReplApp, ReplRunOptions?, CancellationToken)" path="/exception"/>
	public static ValueTask<int> RunAsync(
		ReplApp app,
		System.Net.WebSockets.WebSocket socket,
		ReplRunOptions? options,
		CancellationToken cancellationToken = default) =>
		RunAsync(app, socket, options, onControlMessage: null, cancellationToken);

	/// <summary>
	/// Runs a REPL app session over a WebSocket-backed host using explicit run options
	/// and receives parsed terminal control messages as they arrive.
	/// </summary>
	/// <param name="app">Configured REPL app instance.</param>
	/// <param name="socket">Connected WebSocket.</param>
	/// <param name="options">Run options.</param>
	/// <param name="onControlMessage">Optional observer for parsed terminal control messages.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Execution exit code.</returns>
	/// <inheritdoc cref="StreamedReplHost.RunSessionAsync(ReplApp, ReplRunOptions?, CancellationToken)" path="/exception"/>
	public static ValueTask<int> RunAsync(
		ReplApp app,
		System.Net.WebSockets.WebSocket socket,
		ReplRunOptions? options,
		Action<TerminalControlMessage>? onControlMessage,
		CancellationToken cancellationToken = default) =>
		RunCoreAsync(app, socket, services: null, options, onControlMessage, cancellationToken);

	/// <summary>Runs a WebSocket session using services borrowed from the embedding host.</summary>
	/// <param name="app">Configured REPL application.</param>
	/// <param name="socket">Connected WebSocket.</param>
	/// <param name="services">Host-owned provider; session scopes follow the run options.</param>
	/// <param name="options">Optional run options.</param>
	/// <param name="onControlMessage">Optional terminal metadata observer.</param>
	/// <param name="cancellationToken">Session cancellation.</param>
	/// <returns>Session exit code.</returns>
	public static ValueTask<int> RunWithServicesAsync(ReplApp app, System.Net.WebSockets.WebSocket socket,
		IServiceProvider services, ReplRunOptions? options = null,
		Action<TerminalControlMessage>? onControlMessage = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(services);
		return RunCoreAsync(app, socket, services, options, onControlMessage, cancellationToken);
	}

	private static async ValueTask<int> RunCoreAsync(ReplApp app, System.Net.WebSockets.WebSocket socket,
		IServiceProvider? services, ReplRunOptions? options, Action<TerminalControlMessage>? onControlMessage,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(app);
		ArgumentNullException.ThrowIfNull(socket);

		var writer = new WebSocketTextWriter(socket, cancellationToken);
		var host = new StreamedReplHost(writer) { TransportName = "websocket" };
		host.UpdateTerminalCapabilities(TerminalCapabilities.VtInput);
		var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		try
		{
			var receiveTask = ReceiveLoopAsync(socket, host, onControlMessage, receiveCts.Token);

			int exitCode;
			try
			{
				exitCode = services is null
					? await host.RunSessionAsync(app, options, cancellationToken).ConfigureAwait(false)
					: await host.RunSessionAsync(app, services, options, cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				if (cancellationToken.IsCancellationRequested)
				{
					// A cancelled session aborts the connection, whether or not its receive ever started.
					socket.Abort();
				}
				else
				{
					// A WebSocket aborts a receive that is cancelled, so a pending receive is ended by closing the
					// connection, or by answering the peer's own close; one deadline covers the whole handshake.
					receiveCts.CancelAfter(CloseTimeout);
					if (!receiveTask.IsCompleted)
					{
						await SendCloseAsync(socket, receiveCts.Token).ConfigureAwait(false);
					}
				}

				await receiveTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
				await FinishCloseAsync(socket, receiveCts.Token).ConfigureAwait(false);
				// A failed receive surfaces only once the connection is settled.
				await receiveTask.ConfigureAwait(false);
			}

			return exitCode;
		}
		finally
		{
			await host.DisposeAsync().ConfigureAwait(false);
			receiveCts.Dispose();
		}
	}

	// Sends the session's close; the receive still pending gets the peer's answer.
	private static async Task SendCloseAsync(System.Net.WebSockets.WebSocket socket, CancellationToken deadline)
	{
		if (socket.State is not WebSocketState.Open)
		{
			return;
		}

		try
		{
			await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, deadline)
				.ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is OperationCanceledException or System.Net.WebSockets.WebSocketException or InvalidOperationException)
		{
			// The close could not be sent: the receive ends with the deadline, and finishing the close aborts what is left.
		}
	}

	// Finishes the close handshake within the close's deadline, whatever state the receive left the connection in, and
	// aborts a connection that is still not closed once the deadline passes. Repl.Telnet's WebSocketStream keeps a copy
	// of this in step: the two assemblies share no internal code.
	private static async Task FinishCloseAsync(System.Net.WebSockets.WebSocket socket, CancellationToken deadline)
	{
		try
		{
			var discarded = new byte[256];
			while (socket.State is WebSocketState.CloseSent)
			{
				var result = await socket.ReceiveAsync(discarded.AsMemory(), deadline).ConfigureAwait(false);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					break;
				}
			}

			if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
			{
				await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, deadline).ConfigureAwait(false);
			}
		}
		catch (Exception ex) when (ex is OperationCanceledException or System.Net.WebSockets.WebSocketException or InvalidOperationException)
		{
			// The deadline passed or the connection failed: it is aborted below.
		}

		if (socket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
		{
			socket.Abort();
		}
	}

	private static async Task ReceiveLoopAsync(
		System.Net.WebSockets.WebSocket socket,
		StreamedReplHost host,
		Action<TerminalControlMessage>? onControlMessage,
		CancellationToken cancellationToken)
	{
		var buffer = new byte[4096];
		var characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
		var decoder = Encoding.UTF8.GetDecoder();
		var message = new StringBuilder();
		var messageBytes = 0;
		try
		{
			// Reading goes on after the session's own close, until the peer answers it.
			while (!cancellationToken.IsCancellationRequested
			       && socket.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
			{
				var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					break;
				}

				// Once the session's close is sent, the session has ended: what the peer still sends is not applied.
				if ((result.Count > 0 || result.EndOfMessage) && socket.State is not WebSocketState.CloseSent)
				{
					if (result.Count > MaximumMessageBytes - messageBytes)
					{
						using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
						// Keep the socket deadline active while application cancellation callbacks unwind.
						using var abort = closing.Token.Register(() =>
						{
							if (socket.State is not (WebSocketState.Closed or WebSocketState.Aborted)) { socket.Abort(); }
						});
						closing.CancelAfter(CloseTimeout);
						try { await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Messages must not exceed 64 KiB.", closing.Token).ConfigureAwait(false); }
						finally { host.Interrupt(); }
						break;
					}
					messageBytes += result.Count;
					message.Append(characters, 0, decoder.GetChars(buffer, 0, result.Count, characters, 0, flush: result.EndOfMessage));
					if (!result.EndOfMessage) { continue; }
					var payload = message.ToString();
					message.Clear();
					messageBytes = 0;
					DispatchInput(host, payload, result.MessageType, onControlMessage);
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Expected when session ends.
		}
		catch (System.Net.WebSockets.WebSocketException)
		{
			// Connection dropped.
		}
		finally
		{
			host.Complete();
		}
	}

	private static void DispatchInput(StreamedReplHost host, string payload, WebSocketMessageType type, Action<TerminalControlMessage>? onControlMessage)
	{
		if (type == WebSocketMessageType.Text && TerminalControlProtocol.TryParse(payload, out var controlMessage))
		{
			host.ApplyControlMessage(controlMessage);
			onControlMessage?.Invoke(controlMessage);
			return;
		}
		var segments = payload.Split('\u0003');
		for (var i = 0; i < segments.Length; i++)
		{
			if (segments[i].Length > 0) { host.EnqueueInput(segments[i]); }
			if (i < segments.Length - 1) { host.Interrupt(); }
		}
	}
}
