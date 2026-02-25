namespace Repl;

/// <summary>
/// Host capability toggles.
/// </summary>
public sealed class CapabilityOptions
{
	/// <summary>
	/// Gets or sets a value indicating whether ANSI output should be considered available.
	/// </summary>
	public bool SupportsAnsi { get; set; } = true;
}