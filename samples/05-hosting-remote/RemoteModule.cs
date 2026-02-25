using System.ComponentModel;
using System.Threading.Channels;

using Repl;

using Results = Repl.Results;

namespace HostingRemoteSample;

/// <summary>
/// REPL module demonstrating shared settings, messaging, and session tracking.
/// </summary>
internal sealed class RemoteModule(
	ISettingsService settings,
	IMessageBus bus,
	SessionTracker tracker) : IReplModule
{
	public void Map(IReplMap map)
	{
		map.Context(
			"settings",
			[Description("Read and write shared settings")]
			(IReplMap m) =>
			{
				m.Map(
					"show {key}",
					[Description("Read a setting value")]
					(string key) =>
						settings.Get(key) is { } value
							? Results.Ok($"{key} = {value}")
							: Results.NotFound($"Setting '{key}' not found."));

				m.Map(
					"set {key} {value}",
					[Description("Write a setting value")]
					(string key, string value) =>
					{
						settings.Set(key, value);
						return Results.Success($"Setting '{key}' updated to '{value}'.");
					});
			});

		map.Map(
			"send {message}",
			[Description("Publish a message to all watching sessions")]
			(
				[Description("Message to send")]
				string message) =>
			{
				var sender = $"session-{Environment.CurrentManagedThreadId}";
				bus.Publish(sender, message);
				return Results.Ok("Message sent.");
			});

		map.Map(
			"watch",
			[Description("Subscribe to messages (press Enter to stop)")]
			async (IReplInteractionChannel channel, CancellationToken ct) =>
			{
				var messages = Channel.CreateUnbounded<string>();

				void Handler(string sender, string msg) =>
					messages.Writer.TryWrite($"[{sender}] {msg}");

				bus.OnMessage += Handler;
				try
				{
					using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
					var displayTask = DisplayMessagesAsync(messages.Reader, channel, cts.Token);
					await channel.AskTextAsync("watch-stop", "Watching... press Enter to stop");
					await cts.CancelAsync();
					await displayTask.ConfigureAwait(false);
				}
				finally
				{
					bus.OnMessage -= Handler;
					messages.Writer.Complete();
				}

				return Results.Ok("Stopped watching.");
			});

		map.Map(
			"who",
			[Description("List connected sessions")]
			() =>
			{
				var sessions = tracker.GetAllNames();
				return sessions.Count == 0
					? Results.Ok("No active sessions.")
					: Results.Ok(string.Join('\n', sessions));
			});

		map.Map(
			"sessions",
			[Description("List active sessions with transport and activity details")]
			(IReplSessionInfo session) =>
			{
				tracker.UpdateFromSession(session);
				var sessions = tracker.GetAll();
				return sessions.Count == 0
					? (object)Results.Ok("No active sessions.")
					: sessions.Select(ToSessionRow).ToArray();
			});

		map.Map(
			"status",
			[Description("Show system status (adapts to terminal width)")]
			(IReplSessionInfo session) =>
			{
				tracker.UpdateFromSession(session);
				var sessions = tracker.GetAll();
				return new StatusRow[]
				{
					new("Sessions", $"{sessions.Count} active", sessions.Count > 0 ? "ok" : "idle"),
					new("Settings", $"{settings.Count} keys", "ok"),
					new("Maintenance", settings.Get("maintenance") ?? "unknown", settings.Get("maintenance") == "on" ? "warning" : "ok"),
					new("Uptime", FormatUptime(), "ok"),
					new("Screen", session.WindowSize is { } sz ? $"{sz.Width}x{sz.Height}" : "unknown", "ok"),
					new("Transport", FormatTransport(session), "ok"),
					new("Terminal", FormatTerminal(session), "ok"),
					new("Server", Environment.MachineName, "ok"),
					new("Runtime", $".NET {Environment.Version}", "ok"),
				};
			});
	}

	private static SessionRow ToSessionRow(SessionSnapshot session)
	{
		var now = DateTimeOffset.UtcNow;
		var connectedFor = now - session.ConnectedAtUtc;
		var idleFor = now - session.LastSeenUtc;
		return new SessionRow(
			Name: session.Name,
			Transport: session.Transport,
			Remote: string.IsNullOrWhiteSpace(session.RemotePeer) ? "unknown" : session.RemotePeer!,
			Screen: session.Screen ?? "unknown",
			Terminal: FormatTerminal(session),
			ConnectedFor: FormatDuration(connectedFor),
			IdleFor: FormatDuration(idleFor));
	}

	private static string FormatUptime()
	{
		var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
		return FormatDuration(uptime);
	}

	private static string FormatTerminal(IReplSessionInfo session)
	{
		var caps = session.TerminalCapabilities;
		var identity = session.TerminalIdentity;
		if (!string.IsNullOrWhiteSpace(identity))
		{
			return caps == TerminalCapabilities.None
				? identity
				: $"{identity} ({caps})";
		}

		return caps == TerminalCapabilities.None ? "unknown" : caps.ToString();
	}

	private static string FormatTransport(IReplSessionInfo session)
	{
		var transport = session.TransportName ?? "console";
		if (string.IsNullOrWhiteSpace(session.RemotePeer))
		{
			return transport;
		}

		return $"{transport} ({session.RemotePeer})";
	}

	private static string FormatDuration(TimeSpan value)
	{
		if (value.TotalHours >= 1)
		{
			return $"{(int)value.TotalHours}h {value.Minutes}m";
		}

		if (value.TotalMinutes >= 1)
		{
			return $"{value.Minutes}m {value.Seconds}s";
		}

		return $"{Math.Max(0, value.Seconds)}s";
	}

	private static string FormatTerminal(SessionSnapshot session)
	{
		if (!string.IsNullOrWhiteSpace(session.Terminal))
		{
			return session.Capabilities == TerminalCapabilities.None
				? session.Terminal
				: $"{session.Terminal} ({session.Capabilities})";
		}

		return session.Capabilities == TerminalCapabilities.None
			? "unknown"
			: session.Capabilities.ToString();
	}

	private sealed record SessionRow(
		[property: System.ComponentModel.DataAnnotations.Display(Name = "Session")] string Name,
		[property: System.ComponentModel.DataAnnotations.Display(Name = "Transport")] string Transport,
		[property: System.ComponentModel.DataAnnotations.Display(Name = "Remote")] string Remote,
		[property: System.ComponentModel.DataAnnotations.Display(Name = "Screen")] string Screen,
		[property: System.ComponentModel.DataAnnotations.Display(Name = "Terminal")] string Terminal,
		[property: System.ComponentModel.DataAnnotations.Display(Name = "Connected")] string ConnectedFor,
		[property: System.ComponentModel.DataAnnotations.Display(Name = "Idle")] string IdleFor);

	private sealed record StatusRow(
		[property: System.ComponentModel.DataAnnotations.Display(Name = "Component")] string Component,
		[property: System.ComponentModel.DataAnnotations.Display(Name = "Value")] string Value,
		[property: System.ComponentModel.DataAnnotations.Display(Name = "State")] string State);

	private static async Task DisplayMessagesAsync(
		ChannelReader<string> reader,
		IReplInteractionChannel channel,
		CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var msg in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
			{
				await channel.WriteStatusAsync(msg, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			// Expected when user presses Enter.
		}
	}
}
