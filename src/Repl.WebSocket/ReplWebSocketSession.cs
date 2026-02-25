using System.Net.WebSockets;
using System.Text;

namespace Repl.WebSocket;

/// <summary>
/// Runs a REPL session over a raw WebSocket connection.
/// </summary>
public static class ReplWebSocketSession
{
	/// <summary>
	/// Runs a REPL app session over a WebSocket-backed host.
	/// </summary>
	/// <param name="app">Configured REPL app instance.</param>
	/// <param name="socket">Connected WebSocket.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Execution exit code.</returns>
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
	public static async ValueTask<int> RunAsync(
		ReplApp app,
		System.Net.WebSockets.WebSocket socket,
		ReplRunOptions? options,
		Action<TerminalControlMessage>? onControlMessage,
		CancellationToken cancellationToken = default)
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
				exitCode = await host.RunSessionAsync(app, options, cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				await receiveCts.CancelAsync().ConfigureAwait(false);
				try
				{
					await receiveTask.ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					// Expected at session shutdown.
				}
			}

			return exitCode;
		}
		finally
		{
			await host.DisposeAsync().ConfigureAwait(false);
			receiveCts.Dispose();

			if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
			{
				using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				closeCts.CancelAfter(TimeSpan.FromSeconds(1));
				try
				{
					await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, closeCts.Token)
						.ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					socket.Abort();
				}
				catch (System.Net.WebSockets.WebSocketException)
				{
					socket.Abort();
				}
			}
		}
	}

	private static async Task ReceiveLoopAsync(
		System.Net.WebSockets.WebSocket socket,
		StreamedReplHost host,
		Action<TerminalControlMessage>? onControlMessage,
		CancellationToken cancellationToken)
	{
		var buffer = new byte[4096];
		try
		{
			while (!cancellationToken.IsCancellationRequested
			       && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
			{
				var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					break;
				}

				if (result.Count > 0)
				{
					var payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
					if (result.MessageType == WebSocketMessageType.Text
					    && TerminalControlProtocol.TryParse(payload, out var controlMessage))
					{
						host.ApplyControlMessage(controlMessage);
						onControlMessage?.Invoke(controlMessage);
						continue;
					}

					host.EnqueueInput(payload);
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
}
