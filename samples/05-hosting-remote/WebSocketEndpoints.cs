using System.Net.WebSockets;
using Repl;
using Repl.Telnet;
using Repl.WebSocket;

namespace HostingRemoteSample;

internal static class WebSocketEndpoints
{
	internal static void MapWebSocketTerminals(this WebApplication app)
	{
		app.Map("/ws/repl", (HttpContext context, ReplApp repl, SessionTracker tracker) =>
			RunAsync(context, repl, tracker, app.Lifetime, telnet: false));
		app.Map("/ws/telnet", (HttpContext context, ReplApp repl, SessionTracker tracker) =>
			RunAsync(context, repl, tracker, app.Lifetime, telnet: true));
	}

	private static async Task RunAsync(HttpContext context, ReplApp repl, SessionTracker tracker, IHostApplicationLifetime lifetime, bool telnet)
	{
		if (!context.WebSockets.IsWebSocketRequest)
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		using var socket = await context.WebSockets.AcceptWebSocketAsync();
		await using var sessionLifetime = new SessionLifetime(lifetime.ApplicationStopping, context.RequestAborted);
		var transport = telnet ? "telnet" : "websocket";
		var sessionName = $"{(telnet ? "telnet" : "ws")}-{Guid.NewGuid().ToString("N")[..8]}";
		var remotePeer = FormatRemotePeer(context);
		var metadata = TerminalMetadata.FromQuery(context.Request.Query);
		tracker.Add(sessionName, transport, remotePeer);
		metadata.ApplyTo(tracker, sessionName);
		var options = new ReplRunOptions
		{
			TerminalOverrides = new TerminalSessionOverrides
			{
				TransportName = transport,
				RemotePeer = remotePeer,
				AnsiSupported = telnet ? null : metadata.AnsiSupported,
				TerminalIdentity = telnet ? null : metadata.Terminal,
				WindowSize = telnet ? null : metadata.WindowSize,
			},
		};

		try
		{
			if (telnet)
			{
				await ReplTelnetSession.RunAsync(repl, socket, options,
					onWindowSizeChanged: (cols, rows) => tracker.UpdateMetadataByName(
						sessionName, screen: $"{cols}x{rows}", capabilities: TerminalCapabilities.ResizeReporting),
					onTerminalTypeChanged: terminal => tracker.UpdateMetadataByName(
						sessionName, terminal: terminal,
						capabilities: TerminalCapabilities.Ansi | TerminalCapabilities.IdentityReporting | TerminalCapabilities.VtInput,
						ansiSupported: true),
					cancellationToken: sessionLifetime.Token);
			}
			else
			{
				await ReplWebSocketSession.RunAsync(repl, socket, options,
					message => TerminalMetadata.ApplyControlMessage(tracker, sessionName, message), sessionLifetime.Token);
			}
		}
		catch (OperationCanceledException) when (sessionLifetime.IsCancellationRequested)
		{
			// A disconnected client or host shutdown ends this session.
		}
		finally
		{
			tracker.Remove(sessionName);
			await TryCloseSocketAsync(socket, sessionLifetime.Token);
		}
	}

	private static string? FormatRemotePeer(HttpContext context)
	{
		var address = context.Connection.RemoteIpAddress?.ToString();
		if (string.IsNullOrWhiteSpace(address)) { return null; }
		var formatted = address.Contains(':') && !address.StartsWith('[') ? $"[{address}]" : address;
		return context.Connection.RemotePort > 0 ? $"{formatted}:{context.Connection.RemotePort}" : formatted;
	}

	private static async ValueTask TryCloseSocketAsync(WebSocket socket, CancellationToken cancellationToken)
	{
		if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) { return; }
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(1));
		try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, deadline.Token); }
		catch (OperationCanceledException) { socket.Abort(); }
		catch (WebSocketException) { socket.Abort(); }
	}
}
