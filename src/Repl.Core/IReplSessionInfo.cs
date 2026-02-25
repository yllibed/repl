namespace Repl;

/// <summary>
/// Provides read-only information about the current REPL session.
/// Injectable as a handler parameter to inspect client and transport properties.
/// </summary>
public interface IReplSessionInfo
{
	/// <summary>
	/// Gets the current terminal window size, or <c>null</c> when unknown.
	/// This value updates live as the client resizes.
	/// </summary>
	(int Width, int Height)? WindowSize { get; }

	/// <summary>
	/// Gets a value indicating whether the client supports ANSI/VT escape sequences.
	/// </summary>
	bool AnsiSupported { get; }

	/// <summary>
	/// Gets the transport name (e.g. "websocket", "telnet", "signalr"), or <c>null</c> for local console.
	/// </summary>
	string? TransportName { get; }

	/// <summary>
	/// Gets remote peer information for the active session (for example "203.0.113.7:50124"), or <c>null</c> when unknown.
	/// </summary>
	string? RemotePeer { get; }

	/// <summary>
	/// Gets the terminal capability flags reported or inferred for the current session.
	/// </summary>
	TerminalCapabilities TerminalCapabilities { get; }

	/// <summary>
	/// Gets the terminal identity reported by the client (e.g. "xterm-256color"), or <c>null</c> when unknown.
	/// </summary>
	string? TerminalIdentity { get; }
}
