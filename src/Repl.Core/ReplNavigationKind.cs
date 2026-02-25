namespace Repl;

/// <summary>
/// Supported navigation behaviors for interactive scope transitions.
/// </summary>
public enum ReplNavigationKind
{
	/// <summary>
	/// Navigate one scope level up.
	/// </summary>
	Up = 0,

	/// <summary>
	/// Navigate to an explicit path.
	/// </summary>
	To = 1,
}
