namespace Repl.Interaction;

/// <summary>
/// Controls whether advanced terminal progress sequences are emitted.
/// </summary>
public enum AdvancedProgressMode
{
	/// <summary>
	/// Emit advanced progress sequences automatically in interactive ANSI sessions.
	/// </summary>
	Auto,

	/// <summary>
	/// Force advanced progress sequences in interactive ANSI sessions.
	/// </summary>
	Always,

	/// <summary>
	/// Never emit advanced progress sequences.
	/// </summary>
	Never,
}
