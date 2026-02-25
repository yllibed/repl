namespace Repl;

/// <summary>
/// Defines ANSI rendering behavior.
/// </summary>
public enum AnsiMode
{
	/// <summary>
	/// Auto-detect support.
	/// </summary>
	Auto,

	/// <summary>
	/// Force ANSI output.
	/// </summary>
	Always,

	/// <summary>
	/// Disable ANSI output.
	/// </summary>
	Never,
}