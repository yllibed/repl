using Microsoft.AspNetCore.SignalR;

using Repl;

namespace HostingRemoteSample;

internal sealed class ReplHub(SignalRSessions sessions, SessionTracker tracker) : Hub
{
	public override async Task OnConnectedAsync()
	{
		var host = new StreamedReplHost(new SignalRTextWriter(Clients.Caller))
		{
			TransportName = "signalr",
			RemotePeer = ResolveRemotePeer(),
		};
		host.UpdateTerminalCapabilities(TerminalCapabilities.VtInput);
		var sessionName = $"signalr-{Context.ConnectionId[..8]}";
		var options = new ReplRunOptions
		{
			TerminalOverrides = new TerminalSessionOverrides
			{
				TransportName = "signalr",
				RemotePeer = host.RemotePeer,
			},
		};

		tracker.Add(sessionName, transport: "signalr", host.RemotePeer);
		if (Context.GetHttpContext() is { } context)
		{
			TerminalMetadata.FromQuery(context.Request.Query).ApplyTo(tracker, sessionName);
		}

		try { sessions.Start(Context.ConnectionId, host, sessionName, options, Context.ConnectionAborted); }
		catch
		{
			tracker.Remove(sessionName);
			await host.DisposeAsync();
			throw;
		}

		await base.OnConnectedAsync();
	}

	public void OnInput(string text)
	{
		if (sessions.TryGet(Context.ConnectionId, out var host, out var sessionName))
		{
			if (TerminalControlProtocol.TryParse(text, out var controlMessage))
			{
				host.ApplyControlMessage(controlMessage);
				TerminalMetadata.ApplyControlMessage(tracker, sessionName, controlMessage);
				return;
			}

			host.EnqueueInput(text);
			tracker.Touch(sessionName);
		}
	}

	public override async Task OnDisconnectedAsync(Exception? exception)
	{
		await sessions.DisconnectAsync(Context.ConnectionId);
		await base.OnDisconnectedAsync(exception);
	}

	private string? ResolveRemotePeer()
	{
		var httpContext = Context.GetHttpContext();
		var address = httpContext?.Connection.RemoteIpAddress?.ToString();
		var port = httpContext?.Connection.RemotePort ?? 0;
		if (string.IsNullOrWhiteSpace(address))
		{
			return $"signalr:{Context.ConnectionId}";
		}

		var endpoint = FormatRemoteEndpoint(address, port);
		return $"{endpoint} ({Context.ConnectionId})";
	}

	private static string FormatRemoteEndpoint(string address, int port)
	{
		var formattedAddress = address.Contains(':') && !address.StartsWith('[')
			? $"[{address}]"
			: address;
		return port > 0 ? $"{formattedAddress}:{port}" : formattedAddress;
	}
}
