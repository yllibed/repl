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
