namespace Repl.Telnet;

/// <summary>
/// Telnet option codes for negotiation (RFC 854 and extensions).
/// </summary>
public static class TelnetOption
{
	/// <summary>Binary Transmission (RFC 856).</summary>
	public const byte Binary = 0;

	/// <summary>Echo (RFC 857).</summary>
	public const byte Echo = 1;

	/// <summary>Suppress Go-Ahead (RFC 858).</summary>
	public const byte Sga = 3;

	/// <summary>Terminal-Type (RFC 1091).</summary>
	public const byte TerminalType = 24;

	/// <summary>Negotiate About Window Size (RFC 1073).</summary>
	public const byte Naws = 31;
}
