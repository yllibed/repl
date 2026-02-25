namespace Repl.Telnet;

/// <summary>
/// Telnet command bytes (RFC 854).
/// </summary>
public static class TelnetCommand
{
	/// <summary>End of subnegotiation parameters.</summary>
	public const byte SE = 240;

	/// <summary>Begin subnegotiation.</summary>
	public const byte SB = 250;

	/// <summary>Will perform option.</summary>
	public const byte Will = 251;

	/// <summary>Won't perform option.</summary>
	public const byte Wont = 252;

	/// <summary>Request to perform option.</summary>
	public const byte Do = 253;

	/// <summary>Request not to perform option.</summary>
	public const byte Dont = 254;

	/// <summary>Interpret As Command escape byte.</summary>
	public const byte Iac = 255;
}
