using System.Net.WebSockets;

using HostingRemoteSample;

using Microsoft.Extensions.DependencyInjection;

using Repl;
using Repl.Telnet;
using Repl.WebSocket;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();

// Shared services — available to both ASP.NET endpoints and the REPL module.
builder.Services.AddSingleton<ISettingsService, InMemorySettingsService>();
builder.Services.AddSingleton<IMessageBus, InMemoryMessageBus>();
builder.Services.AddSingleton<SessionTracker>();

// REPL application — resolved lazily so the module can pull from host DI.
builder.Services.AddRepl((sp, replApp) =>
{
	replApp.UseEmbeddedConsoleProfile();
	replApp.WithDescription("Remote REPL sample — settings, messaging, session tracking.");
	replApp.MapModule(new RemoteModule(
		sp.GetRequiredService<ISettingsService>(),
		sp.GetRequiredService<IMessageBus>(),
		sp.GetRequiredService<SessionTracker>()));
});

var webApp = builder.Build();

webApp.UseWebSockets();
webApp.UseDefaultFiles();
webApp.UseStaticFiles();

// Raw WebSocket endpoint
webApp.Map("/ws/repl", async (HttpContext context, ReplApp repl, SessionTracker tracker) =>
{
	if (!context.WebSockets.IsWebSocketRequest)
	{
		context.Response.StatusCode = 400;
		return;
	}

	var socket = await context.WebSockets.AcceptWebSocketAsync();
	var sessionName = $"ws-{Guid.NewGuid().ToString("N")[..8]}";
	var remotePeer = FormatRemotePeer(context);
	tracker.Add(sessionName, transport: "websocket", remotePeer);
	ApplyInitialMetadata(tracker, sessionName, context.Request.Query);
	var options = new ReplRunOptions
	{
		TerminalOverrides = new TerminalSessionOverrides
		{
			TransportName = "websocket",
			RemotePeer = remotePeer,
		},
	};

	try
	{
		await ReplWebSocketSession.RunAsync(
			repl,
			socket,
			options,
			controlMessage => ApplyControlMessageMetadata(tracker, sessionName, controlMessage),
			context.RequestAborted);
	}
	finally
	{
		tracker.Remove(sessionName);
		await TryCloseSocketAsync(socket, context.RequestAborted);
	}
});

// Telnet-over-WebSocket endpoint (NAWS + ECHO + SGA + BINARY)
webApp.Map("/ws/telnet", async (HttpContext context, ReplApp repl, SessionTracker tracker) =>
{
	if (!context.WebSockets.IsWebSocketRequest)
	{
		context.Response.StatusCode = 400;
		return;
	}

	var socket = await context.WebSockets.AcceptWebSocketAsync();
	var sessionName = $"telnet-{Guid.NewGuid().ToString("N")[..8]}";
	var remotePeer = FormatRemotePeer(context);
	tracker.Add(sessionName, transport: "telnet", remotePeer);
	ApplyInitialMetadata(tracker, sessionName, context.Request.Query);
	var options = new ReplRunOptions
	{
		TerminalOverrides = new TerminalSessionOverrides
		{
			TransportName = "telnet",
			RemotePeer = remotePeer,
		},
	};

	try
	{
		await ReplTelnetSession.RunAsync(
			repl,
			socket,
			options,
			onWindowSizeChanged: (cols, rows) =>
				tracker.UpdateMetadataByName(
					sessionName,
					screen: $"{cols}x{rows}",
					capabilities: TerminalCapabilities.ResizeReporting),
			onTerminalTypeChanged: terminal =>
				tracker.UpdateMetadataByName(
					sessionName,
					terminal: terminal,
					capabilities: TerminalCapabilities.Ansi | TerminalCapabilities.IdentityReporting | TerminalCapabilities.VtInput,
					ansiSupported: true),
			cancellationToken: context.RequestAborted);
	}
	finally
	{
		tracker.Remove(sessionName);
		await TryCloseSocketAsync(socket, context.RequestAborted);
	}
});

// SignalR endpoint
webApp.MapHub<ReplHub>("/hub/repl");

webApp.Run();

static string? FormatRemotePeer(HttpContext context)
{
	var address = context.Connection.RemoteIpAddress?.ToString();
	var port = context.Connection.RemotePort;
	if (string.IsNullOrWhiteSpace(address))
	{
		return null;
	}

	return FormatRemoteEndpoint(address, port);
}

static async ValueTask TryCloseSocketAsync(WebSocket socket, CancellationToken requestAborted)
{
	if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
	{
		return;
	}

	using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
	closeCts.CancelAfter(TimeSpan.FromSeconds(1));

	try
	{
		await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, closeCts.Token);
	}
	catch (OperationCanceledException)
	{
		socket.Abort();
	}
	catch (WebSocketException)
	{
		socket.Abort();
	}
}

static void ApplyInitialMetadata(SessionTracker tracker, string sessionName, IQueryCollection query)
{
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

static void ApplyControlMessageMetadata(
	SessionTracker tracker,
	string sessionName,
	TerminalControlMessage controlMessage)
{
	tracker.UpdateMetadataByName(
		sessionName,
		terminal: controlMessage.TerminalIdentity,
		screen: controlMessage.WindowSize is { } size ? $"{size.Width}x{size.Height}" : null,
		capabilities: controlMessage.TerminalCapabilities,
		ansiSupported: controlMessage.AnsiSupported);
}

static string? BuildScreen(string? colsRaw, string? rowsRaw)
{
	var cols = ParsePositiveInt(colsRaw);
	var rows = ParsePositiveInt(rowsRaw);
	return cols is > 0 && rows is > 0 ? $"{cols}x{rows}" : null;
}

static int? ParsePositiveInt(string? raw) =>
	int.TryParse(raw, out var value) && value > 0 ? value : null;

static bool? ParseBoolean(string? raw) =>
	bool.TryParse(raw, out var value) ? value : null;

static TerminalCapabilities? ParseCapabilities(string? raw)
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

static string FormatRemoteEndpoint(string address, int port)
{
	var formattedAddress = address.Contains(':') && !address.StartsWith('[')
		? $"[{address}]"
		: address;
	return port > 0 ? $"{formattedAddress}:{port}" : formattedAddress;
}
