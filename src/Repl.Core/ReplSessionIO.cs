using System.Collections.Concurrent;

namespace Repl;

/// <summary>
/// Provides per-session I/O isolation using <see cref="AsyncLocal{T}"/>.
/// When a session is active (hosted mode), reads and writes go through the session's
/// <see cref="TextWriter"/> and <see cref="TextReader"/>.
/// When no session is active (console mode), falls back to <see cref="Console.Out"/>
/// and <see cref="Console.In"/>.
/// </summary>
internal static class ReplSessionIO
{
	internal readonly record struct SessionMetadata(
		string SessionId,
		bool? AnsiSupport,
		(int Width, int Height)? WindowSize,
		string? TransportName,
		string? RemotePeer,
		string? TerminalIdentity,
		TerminalCapabilities TerminalCapabilities,
		DateTimeOffset LastUpdatedUtc);

	private static readonly AsyncLocal<TextWriter?> s_output = new();
	private static readonly AsyncLocal<TextReader?> s_input = new();
	private static readonly AsyncLocal<IReplKeyReader?> s_keyReader = new();
	private static readonly AsyncLocal<string?> s_sessionId = new();
	private static readonly ConcurrentDictionary<string, SessionMetadata> s_sessions = new(StringComparer.Ordinal);

	/// <summary>
	/// Gets the current session output writer, or <see cref="Console.Out"/> when no session is active.
	/// </summary>
	public static TextWriter Output => s_output.Value ?? Console.Out;

	/// <summary>
	/// Gets the current session input reader, or <see cref="Console.In"/> when no session is active.
	/// </summary>
	public static TextReader Input => s_input.Value ?? Console.In;

	/// <summary>
	/// Gets a value indicating whether a hosted session is active on the current async context.
	/// </summary>
	public static bool IsSessionActive => s_output.Value is not null && !string.IsNullOrWhiteSpace(s_sessionId.Value);

	/// <summary>
	/// Gets the current hosted session identifier, when available.
	/// </summary>
	public static string? CurrentSessionId => s_sessionId.Value;

	/// <summary>
	/// Per-session ANSI support override. Null means use default detection.
	/// </summary>
	public static bool? AnsiSupport
	{
		get => GetCurrentSession()?.AnsiSupport;
		set
		{
			if (TryGetCurrentSessionId(out var sessionId))
			{
				UpdateSession(
					sessionId,
					session =>
					{
						var capabilities = value switch
						{
							true => session.TerminalCapabilities | TerminalCapabilities.Ansi,
							false => session.TerminalCapabilities & ~TerminalCapabilities.Ansi,
							_ => session.TerminalCapabilities,
						};
						return session with { AnsiSupport = value, TerminalCapabilities = capabilities };
					});
			}
		}
	}

	/// <summary>
	/// Per-session terminal size override. Null means use <see cref="Console.WindowWidth"/> fallback.
	/// </summary>
	public static (int Width, int Height)? WindowSize
	{
		get => GetCurrentSession()?.WindowSize;
		set
		{
			if (TryGetCurrentSessionId(out var sessionId))
			{
				UpdateSession(
					sessionId,
					session =>
					{
						var capabilities = value is { }
							? session.TerminalCapabilities | TerminalCapabilities.ResizeReporting
							: session.TerminalCapabilities;
						return session with { WindowSize = value, TerminalCapabilities = capabilities };
					});
			}
		}
	}

	/// <summary>
	/// Per-session key reader for remote line editing. Null means use console fallback.
	/// </summary>
	public static IReplKeyReader? KeyReader
	{
		get => s_keyReader.Value;
		set => s_keyReader.Value = value;
	}

	/// <summary>
	/// Per-session transport name (e.g. "websocket", "telnet", "signalr"). Null means local console.
	/// </summary>
	public static string? TransportName
	{
		get => GetCurrentSession()?.TransportName;
		set
		{
			if (TryGetCurrentSessionId(out var sessionId))
			{
				UpdateSession(sessionId, session => session with { TransportName = value });
			}
		}
	}

	/// <summary>
	/// Per-session remote peer information (for example IP:port). Null means unknown.
	/// </summary>
	public static string? RemotePeer
	{
		get => GetCurrentSession()?.RemotePeer;
		set
		{
			if (TryGetCurrentSessionId(out var sessionId))
			{
				UpdateSession(sessionId, session => session with { RemotePeer = value });
			}
		}
	}

	/// <summary>
	/// Per-session terminal identity reported by the client (e.g. "xterm-256color"). Null means unknown.
	/// </summary>
	public static string? TerminalIdentity
	{
		get => GetCurrentSession()?.TerminalIdentity;
		set
		{
			if (TryGetCurrentSessionId(out var sessionId))
			{
				UpdateSession(
					sessionId,
					session =>
					{
						var inferred = TerminalCapabilitiesClassifier.InferFromIdentity(value);
						var capabilities = session.TerminalCapabilities | inferred;
						return session with { TerminalIdentity = value, TerminalCapabilities = capabilities };
					});
			}
		}
	}

	/// <summary>
	/// Per-session terminal capability flags.
	/// </summary>
	public static TerminalCapabilities TerminalCapabilities
	{
		get => GetCurrentSession()?.TerminalCapabilities ?? TerminalCapabilities.None;
		set
		{
			if (TryGetCurrentSessionId(out var sessionId))
			{
				UpdateSession(sessionId, session => session with { TerminalCapabilities = value });
			}
		}
	}

	/// <summary>
	/// Activates a hosted session on the current async context.
	/// Dispose the returned scope to deactivate.
	/// </summary>
	public static IDisposable SetSession(
		TextWriter output,
		TextReader input,
		AnsiMode ansiMode = AnsiMode.Auto,
		string? sessionId = null)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(input);

		var previousOutput = s_output.Value;
		var previousInput = s_input.Value;
		var previousKeyReader = s_keyReader.Value;
		var previousSessionId = s_sessionId.Value;

		var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId)
			? $"session-{Guid.NewGuid():N}"
			: sessionId;

		EnsureSession(resolvedSessionId);
		s_output.Value = output;
		s_input.Value = input;
		s_sessionId.Value = resolvedSessionId;

		if (ansiMode == AnsiMode.Always)
		{
			UpdateSession(
				resolvedSessionId,
				session => session with
				{
					AnsiSupport = true,
					TerminalCapabilities = session.TerminalCapabilities | TerminalCapabilities.Ansi,
				});
		}
		else if (ansiMode == AnsiMode.Never)
		{
			UpdateSession(
				resolvedSessionId,
				session => session with
				{
					AnsiSupport = false,
					TerminalCapabilities = session.TerminalCapabilities & ~TerminalCapabilities.Ansi,
				});
		}

		return new SessionScope(
			previousOutput,
			previousInput,
			previousKeyReader,
			previousSessionId,
			removeSessionOnDispose: string.IsNullOrWhiteSpace(sessionId),
			sessionIdToRemove: resolvedSessionId);
	}

	internal static void EnsureSession(string sessionId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		s_sessions.TryAdd(sessionId, CreateMetadata(sessionId));
	}

	internal static void RemoveSession(string sessionId)
	{
		if (!string.IsNullOrWhiteSpace(sessionId))
		{
			s_sessions.TryRemove(sessionId, out _);
		}
	}

	internal static void UpdateSession(string sessionId, Func<SessionMetadata, SessionMetadata> updater)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(updater);

		s_sessions.AddOrUpdate(
			sessionId,
			static (id, localUpdater) => NormalizeSession(id, localUpdater(CreateMetadata(id))),
			static (id, session, localUpdater) => NormalizeSession(id, localUpdater(session)),
			updater);
	}

	internal static bool TryGetSession(string sessionId, out SessionMetadata session) =>
		s_sessions.TryGetValue(sessionId, out session);

	private static SessionMetadata? GetCurrentSession()
	{
		return TryGetCurrentSessionId(out var sessionId) && s_sessions.TryGetValue(sessionId, out var session)
			? session
			: null;
	}

	private static bool TryGetCurrentSessionId(out string sessionId)
	{
		if (!string.IsNullOrWhiteSpace(s_sessionId.Value))
		{
			sessionId = s_sessionId.Value!;
			return true;
		}

		sessionId = string.Empty;
		return false;
	}

	private static SessionMetadata CreateMetadata(string sessionId) =>
		new(
			SessionId: sessionId,
			AnsiSupport: null,
			WindowSize: null,
			TransportName: null,
			RemotePeer: null,
			TerminalIdentity: null,
			TerminalCapabilities: TerminalCapabilities.None,
			LastUpdatedUtc: DateTimeOffset.UtcNow);

	private static SessionMetadata NormalizeSession(string sessionId, SessionMetadata session) =>
		session with { SessionId = sessionId, LastUpdatedUtc = DateTimeOffset.UtcNow };

	private sealed class SessionScope(
		TextWriter? previousOutput,
		TextReader? previousInput,
		IReplKeyReader? previousKeyReader,
		string? previousSessionId,
		bool removeSessionOnDispose,
		string sessionIdToRemove) : IDisposable
	{
		public void Dispose()
		{
			s_output.Value = previousOutput;
			s_input.Value = previousInput;
			s_keyReader.Value = previousKeyReader;
			s_sessionId.Value = previousSessionId;

			if (removeSessionOnDispose)
			{
				RemoveSession(sessionIdToRemove);
			}
		}
	}
}
