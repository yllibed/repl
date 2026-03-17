namespace Repl.Spectre;

/// <summary>
/// Configuration options for the Spectre.Console integration.
/// </summary>
public sealed class SpectreConsoleOptions
{
	/// <summary>
	/// Gets or sets a value indicating whether Unicode box-drawing characters
	/// and symbols are used. When <c>false</c>, Spectre falls back to ASCII.
	/// Default is <c>true</c>.
	/// </summary>
	public bool Unicode { get; set; } = true;
}
