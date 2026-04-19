namespace Repl;

/// <summary>
/// Immutable snapshot of the current REPL logging context.
/// </summary>
public sealed record ReplLogContext(
	string? SessionId,
	bool IsSessionActive,
	bool IsHostedSession,
	bool IsProgrammatic,
	bool IsProtocolPassthrough,
	string? TransportName,
	string? RemotePeer,
	string? TerminalIdentity,
	TextWriter Output,
	TextWriter Error);
