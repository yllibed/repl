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
		ApplyInitialMetadataFromQuery(sessionName);

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
				tracker.UpdateMetadataByName(
					sessionName,
					terminal: controlMessage.TerminalIdentity,
					screen: controlMessage.WindowSize is { } size ? $"{size.Width}x{size.Height}" : null,
					capabilities: controlMessage.TerminalCapabilities,
					ansiSupported: controlMessage.AnsiSupported);
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

	private void ApplyInitialMetadataFromQuery(string sessionName)
	{
		var query = Context.GetHttpContext()?.Request.Query;
		if (query is null)
		{
			return;
		}

		var terminal = query["terminal"].ToString();
		var screen = BuildScreen(query["cols"].ToString(), query["rows"].ToString());
		var ansiSupported = ParseBoolean(query["ansi"].ToString());
		var capabilities = ParseCapabilities(query["capabilities"].ToString());

		if (string.IsNullOrWhiteSpace(terminal)
		    && screen is null
		    && ansiSupported is null
		    && capabilities is null)
		{
			return;
		}

		tracker.UpdateMetadataByName(
			sessionName,
			terminal: string.IsNullOrWhiteSpace(terminal) ? null : terminal,
			screen: screen,
			capabilities: capabilities,
			ansiSupported: ansiSupported);
	}

	private static string? BuildScreen(string? colsRaw, string? rowsRaw)
	{
		var cols = ParsePositiveInt(colsRaw);
		var rows = ParsePositiveInt(rowsRaw);
		return cols is > 0 && rows is > 0 ? $"{cols}x{rows}" : null;
	}

	private static int? ParsePositiveInt(string? raw) =>
		int.TryParse(raw, out var value) && value > 0 ? value : null;

	private static bool? ParseBoolean(string? raw) =>
		bool.TryParse(raw, out var value) ? value : null;

	private static TerminalCapabilities? ParseCapabilities(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}

		var parsed = TerminalCapabilities.None;
		foreach (var token in raw.Split([',', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (Enum.TryParse<TerminalCapabilities>(token, ignoreCase: true, out var capability))
			{
				parsed |= capability;
			}
		}

		return parsed == TerminalCapabilities.None ? null : parsed;
	}

	private static string FormatRemoteEndpoint(string address, int port)
	{
		var formattedAddress = address.Contains(':') && !address.StartsWith('[')
			? $"[{address}]"
			: address;
		return port > 0 ? $"{formattedAddress}:{port}" : formattedAddress;
	}
}
