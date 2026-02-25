namespace Repl;

/// <summary>
/// Capability flags reported or inferred for the active terminal session.
/// </summary>
[Flags]
public enum TerminalCapabilities
{
	/// <summary>No terminal capabilities are known.</summary>
	None = 0,

	/// <summary>Terminal supports ANSI/VT escape sequences.</summary>
	Ansi = 1 << 0,

	/// <summary>Terminal reports window resize events.</summary>
	ResizeReporting = 1 << 1,

	/// <summary>Terminal identity has been reported (for example xterm-256color).</summary>
	IdentityReporting = 1 << 2,

	/// <summary>Terminal sends VT-style input sequences (arrows/home/end/etc.).</summary>
	VtInput = 1 << 3,
}
