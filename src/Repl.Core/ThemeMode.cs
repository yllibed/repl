namespace Repl;

/// <summary>
/// Defines terminal theme behavior for ANSI palette selection.
/// </summary>
public enum ThemeMode
{
	/// <summary>
	/// Use automatic theme selection heuristics.
	/// </summary>
	Auto,

	/// <summary>
	/// Force a dark-background palette.
	/// </summary>
	Dark,

	/// <summary>
	/// Force a light-background palette.
	/// </summary>
	Light,
}
