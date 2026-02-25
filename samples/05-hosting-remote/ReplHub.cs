using System.Collections.Concurrent;

using Microsoft.AspNetCore.SignalR;

using Repl;

namespace HostingRemoteSample;

public sealed class ReplHub(ReplApp app, SessionTracker tracker) : Hub
{
	private static readonly ConcurrentDictionary<string, SessionState> s_sessions = new();

	public override async Task OnConnectedAsync()
	{
		var cts = new CancellationTokenSource();
		var host = new StreamedReplHost(new SignalRTextWriter(Clients.Caller))
		{
			TransportName = "signalr",
			RemotePeer = ResolveRemotePeer(),
		};
		host.UpdateTerminalCapabilities(TerminalCapabilities.VtInput);
		var sessionName = $"signalr-{Context.ConnectionId[..8]}";
		var state = new SessionState(host, cts, sessionName);
		var options = new ReplRunOptions
		{
			TerminalOverrides = new TerminalSessionOverrides
			{
				TransportName = "signalr",
				RemotePeer = host.RemotePeer,
			},
		};

		s_sessions[Context.ConnectionId] = state;
		tracker.Add(sessionName, transport: "signalr", host.RemotePeer);
		ApplyInitialMetadataFromQuery(sessionName);

		state.RunTask = Task.Run(async () =>
		{
			try
			{
				await host.RunSessionAsync(app, options, cts.Token);
			}
			catch (OperationCanceledException)
			{
				// Session closed.
			}
			finally
			{
				await host.DisposeAsync();
			}
		});

		await base.OnConnectedAsync();
	}

	public void OnInput(string text)
	{
		if (s_sessions.TryGetValue(Context.ConnectionId, out var state))
		{
			if (TerminalControlProtocol.TryParse(text, out var controlMessage))
			{
				state.Host.ApplyControlMessage(controlMessage);
				tracker.UpdateMetadataByName(
					state.SessionName,
					terminal: controlMessage.TerminalIdentity,
					screen: controlMessage.WindowSize is { } size ? $"{size.Width}x{size.Height}" : null,
					capabilities: controlMessage.TerminalCapabilities,
					ansiSupported: controlMessage.AnsiSupported);
				return;
			}

			state.Host.EnqueueInput(text);
			tracker.Touch(state.SessionName);
		}
	}

	public override async Task OnDisconnectedAsync(Exception? exception)
	{
		if (s_sessions.TryRemove(Context.ConnectionId, out var state))
		{
			state.Host.Complete();
			tracker.Remove(state.SessionName);
			await state.Cts.CancelAsync();
			state.Cts.Dispose();
		}

		await base.OnDisconnectedAsync(exception);
	}

	private sealed record SessionState(StreamedReplHost Host, CancellationTokenSource Cts, string SessionName)
	{
		public Task? RunTask { get; set; }
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
