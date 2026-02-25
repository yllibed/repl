using System.Collections.Concurrent;

using Repl;

namespace HostingRemoteSample;

public interface ISettingsService
{
	string? Get(string key);
	void Set(string key, string value);
	int Count { get; }
}

internal sealed class InMemorySettingsService : ISettingsService
{
	private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.OrdinalIgnoreCase)
	{
		["maintenance"] = "off",
	};

	public int Count => _store.Count;

	public string? Get(string key) => _store.GetValueOrDefault(key);

	public void Set(string key, string value) => _store[key] = value;
}

public interface IMessageBus
{
	event Action<string, string>? OnMessage;

	void Publish(string sender, string message);
}

internal sealed class InMemoryMessageBus : IMessageBus
{
	public event Action<string, string>? OnMessage;

	public void Publish(string sender, string message) => OnMessage?.Invoke(sender, message);
}

public sealed class SessionTracker
{
	private readonly ConcurrentDictionary<string, SessionSnapshot> _sessions = new(StringComparer.Ordinal);

	public void Add(string name, string transport, string? remotePeer)
	{
		var now = DateTimeOffset.UtcNow;
		_sessions[name] = new SessionSnapshot(
			Name: name,
			Transport: transport,
			RemotePeer: remotePeer,
			ConnectedAtUtc: now,
			LastSeenUtc: now,
			Terminal: null,
			Screen: null,
			Capabilities: TerminalCapabilities.None,
			AnsiSupported: null);
	}

	public void Touch(string name)
	{
		_sessions.AddOrUpdate(
			name,
			static id => new SessionSnapshot(id, "unknown", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, TerminalCapabilities.None, null),
			static (id, current) => current with { LastSeenUtc = DateTimeOffset.UtcNow });
	}

	public void UpdateFromSession(IReplSessionInfo session)
	{
		ArgumentNullException.ThrowIfNull(session);

		var sessionName = ResolveSessionName(session.TransportName, session.RemotePeer);
		if (sessionName is null)
		{
			return;
		}

		_sessions.AddOrUpdate(
			sessionName,
			static (id, state) => new SessionSnapshot(
				id,
				"unknown",
				null,
				DateTimeOffset.UtcNow,
				DateTimeOffset.UtcNow,
				state.Terminal,
				state.Screen,
				state.Capabilities,
				state.AnsiSupported),
			static (id, current, state) => current with
			{
				LastSeenUtc = DateTimeOffset.UtcNow,
				Terminal = state.Terminal,
				Screen = state.Screen,
				Capabilities = state.Capabilities,
				AnsiSupported = state.AnsiSupported,
			},
			(
				Terminal: session.TerminalIdentity,
				Screen: session.WindowSize is { } sz ? $"{sz.Width}x{sz.Height}" : null,
				Capabilities: session.TerminalCapabilities,
				AnsiSupported: session.AnsiSupported));
	}

	public void UpdateMetadataByName(
		string sessionName,
		string? terminal = null,
		string? screen = null,
		TerminalCapabilities? capabilities = null,
		bool? ansiSupported = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);

		_sessions.AddOrUpdate(
			sessionName,
			static (id, state) => new SessionSnapshot(
				Name: id,
				Transport: "unknown",
				RemotePeer: null,
				ConnectedAtUtc: DateTimeOffset.UtcNow,
				LastSeenUtc: DateTimeOffset.UtcNow,
				Terminal: state.Terminal,
				Screen: state.Screen,
				Capabilities: state.Capabilities ?? TerminalCapabilities.None,
				AnsiSupported: state.AnsiSupported),
			static (id, current, state) => current with
			{
				LastSeenUtc = DateTimeOffset.UtcNow,
				Terminal = state.Terminal ?? current.Terminal,
				Screen = state.Screen ?? current.Screen,
				Capabilities = state.Capabilities is { } caps
					? current.Capabilities | caps
					: current.Capabilities,
				AnsiSupported = state.AnsiSupported ?? current.AnsiSupported,
			},
			(Terminal: terminal, Screen: screen, Capabilities: capabilities, AnsiSupported: ansiSupported));
	}

	public void Remove(string name) => _sessions.TryRemove(name, out _);

	public IReadOnlyList<SessionSnapshot> GetAll() =>
		[.. _sessions.Values.OrderBy(item => item.Name, StringComparer.Ordinal)];

	public IReadOnlyList<string> GetAllNames() => [.. _sessions.Keys.Order()];

	private string? ResolveSessionName(string? transport, string? remotePeer)
	{
		if (string.IsNullOrWhiteSpace(transport))
		{
			return null;
		}

		var candidates = _sessions.Values
			.Where(item => string.Equals(item.Transport, transport, StringComparison.OrdinalIgnoreCase))
			.ToArray();

		if (!string.IsNullOrWhiteSpace(remotePeer))
		{
			var exact = candidates
				.FirstOrDefault(item => string.Equals(item.RemotePeer, remotePeer, StringComparison.OrdinalIgnoreCase));
			if (exact is not null)
			{
				return exact.Name;
			}
		}

		if (candidates.Length == 1)
		{
			return candidates[0].Name;
		}

		return null;
	}
}

public sealed record SessionSnapshot(
	string Name,
	string Transport,
	string? RemotePeer,
	DateTimeOffset ConnectedAtUtc,
	DateTimeOffset LastSeenUtc,
	string? Terminal,
	string? Screen,
	TerminalCapabilities Capabilities,
	bool? AnsiSupported);
