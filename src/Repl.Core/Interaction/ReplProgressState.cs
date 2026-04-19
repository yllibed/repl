namespace Repl.Interaction;

/// <summary>
/// Describes the semantic state of a progress update.
/// </summary>
public enum ReplProgressState
{
	/// <summary>
	/// Normal in-flight progress.
	/// </summary>
	Normal,

	/// <summary>
	/// Progress update in a warning state.
	/// </summary>
	Warning,

	/// <summary>
	/// Progress update in an error state.
	/// </summary>
	Error,

	/// <summary>
	/// Operation is active but no percentage is currently known.
	/// </summary>
	Indeterminate,

	/// <summary>
	/// Clears any currently displayed progress indicator.
	/// </summary>
	Clear,
}
