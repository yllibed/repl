namespace Repl;

/// <summary>
/// Optional per-run overrides for terminal session metadata.
/// When specified, overrides take precedence over auto-detection.
/// </summary>
public sealed record TerminalSessionOverrides
{
	/// <summary>
	/// Force transport name (for example "websocket", "telnet", "signalr").
	/// </summary>
	public string? TransportName { get; init; }

	/// <summary>
	/// Force remote peer descriptor (for example "203.0.113.7:50124").
	/// </summary>
	public string? RemotePeer { get; init; }

	/// <summary>
	/// Force terminal identity (for example "xterm-256color").
	/// </summary>
	public string? TerminalIdentity { get; init; }

	/// <summary>
	/// Force terminal capability flags.
	/// </summary>
	public TerminalCapabilities? TerminalCapabilities { get; init; }

	/// <summary>
	/// Force ANSI support state.
	/// </summary>
	public bool? AnsiSupported { get; init; }

	/// <summary>
	/// Force terminal window size.
	/// </summary>
	public (int Width, int Height)? WindowSize { get; init; }
}
