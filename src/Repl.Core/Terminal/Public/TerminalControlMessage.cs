namespace Repl.Terminal;

/// <summary>
/// Parsed remote terminal metadata/control payload.
/// </summary>
public sealed record TerminalControlMessage(
	TerminalControlMessageKind Kind,
	string? TerminalIdentity = null,
	(int Width, int Height)? WindowSize = null,
	bool? AnsiSupported = null,
	TerminalCapabilities? TerminalCapabilities = null);
