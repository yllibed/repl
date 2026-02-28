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
	private static readonly AsyncLocal<TextWriter?> s_error = new();
	private static readonly AsyncLocal<TextWriter?> s_commandOutput = new();
	private static readonly AsyncLocal<TextReader?> s_input = new();
	private static readonly AsyncLocal<IReplKeyReader?> s_keyReader = new();
	private static readonly AsyncLocal<bool> s_isHostedSession = new();
	private static readonly AsyncLocal<string?> s_sessionId = new();
	private static readonly ConcurrentDictionary<string, SessionMetadata> s_sessions = new(StringComparer.Ordinal);

	/// <summary>
	/// Gets the current session output writer, or <see cref="Console.Out"/> when no session is active.
	/// </summary>
	public static TextWriter Output => s_output.Value ?? Console.Out;

	/// <summary>
	/// Gets the current session error writer, or <see cref="Console.Error"/> when no session is active.
	/// </summary>
	public static TextWriter Error => s_error.Value ?? Console.Error;

	/// <summary>
	/// Gets the handler output writer. In protocol passthrough mode this can remain bound to stdout
	/// while framework output is redirected to stderr.
	/// </summary>
	/// <remarks>
	/// Falls back to <see cref="Output"/>, which itself falls back to <see cref="Console.Out"/>.
	/// </remarks>
	public static TextWriter CommandOutput => s_commandOutput.Value ?? Output;

	/// <summary>
	/// Gets the current session input reader, or <see cref="Console.In"/> when no session is active.
	/// </summary>
	public static TextReader Input => s_input.Value ?? Console.In;

	/// <summary>
	/// Gets a value indicating whether a hosted session is active on the current async context.
	/// </summary>
	public static bool IsSessionActive => s_output.Value is not null && !string.IsNullOrWhiteSpace(s_sessionId.Value);

	/// <summary>
	/// Gets a value indicating whether execution is currently running in a real hosted transport session.
	/// </summary>
	public static bool IsHostedSession => s_isHostedSession.Value;

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
		string? sessionId = null,
		TextWriter? commandOutput = null,
		TextWriter? error = null,
		bool isHostedSession = true)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(input);

		var previousOutput = s_output.Value;
		var previousError = s_error.Value;
		var previousCommandOutput = s_commandOutput.Value;
		var previousInput = s_input.Value;
		var previousKeyReader = s_keyReader.Value;
		var previousIsHostedSession = s_isHostedSession.Value;
		var previousSessionId = s_sessionId.Value;

		var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId)
			? $"session-{Guid.NewGuid():N}"
			: sessionId;

		EnsureSession(resolvedSessionId);
		s_output.Value = output;
		// Default to the active session output for hosted flows unless a separate error writer is supplied.
		s_error.Value = error ?? output;
		s_commandOutput.Value = commandOutput ?? output;
		s_input.Value = input;
		s_isHostedSession.Value = isHostedSession;
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
			previousError,
			previousCommandOutput,
			previousInput,
			previousKeyReader,
			previousIsHostedSession,
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
		TextWriter? previousError,
		TextWriter? previousCommandOutput,
		TextReader? previousInput,
		IReplKeyReader? previousKeyReader,
		bool previousIsHostedSession,
		string? previousSessionId,
		bool removeSessionOnDispose,
		string sessionIdToRemove) : IDisposable
	{
		public void Dispose()
		{
			s_output.Value = previousOutput;
			s_error.Value = previousError;
			s_commandOutput.Value = previousCommandOutput;
			s_input.Value = previousInput;
			s_keyReader.Value = previousKeyReader;
			s_isHostedSession.Value = previousIsHostedSession;
			s_sessionId.Value = previousSessionId;

			if (removeSessionOnDispose)
			{
				RemoveSession(sessionIdToRemove);
			}
		}
	}
}
