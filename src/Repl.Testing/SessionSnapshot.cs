namespace Repl.Testing;

/// <summary>
/// Immutable snapshot of a live simulated session.
/// </summary>
public sealed record SessionSnapshot(
	string SessionId,
	string? Transport,
	string? Remote,
	string? Terminal,
	(int Width, int Height)? Screen,
	TerminalCapabilities Capabilities,
	bool? AnsiSupported,
	DateTimeOffset LastUpdatedUtc)
{
	/// <summary>
	/// A snapshot for a session that has registered no terminal metadata yet — every field unset and
	/// <see cref="LastUpdatedUtc"/> at <see cref="DateTimeOffset.MinValue"/>.
	/// </summary>
	/// <param name="sessionId">The session the snapshot describes.</param>
	/// <returns>An empty snapshot.</returns>
	public static SessionSnapshot Empty(string sessionId) =>
		new(
			sessionId,
			Transport: null,
			Remote: null,
			Terminal: null,
			Screen: null,
			Capabilities: TerminalCapabilities.None,
			AnsiSupported: null,
			LastUpdatedUtc: DateTimeOffset.MinValue);
}
