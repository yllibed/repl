namespace Repl;

/// <summary>
/// Defines how interactive mode is entered.
/// </summary>
public enum InteractivePolicy
{
	/// <summary>
	/// Uses framework default mode selection.
	/// </summary>
	Auto,

	/// <summary>
	/// Forces interactive mode.
	/// </summary>
	Force,

	/// <summary>
	/// Prevents interactive mode.
	/// </summary>
	Prevent,
}