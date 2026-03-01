namespace Repl.Terminal;

/// <summary>
/// Indicates the kind of terminal control message received from a remote client.
/// </summary>
public enum TerminalControlMessageKind
{
	/// <summary>
	/// Initial capabilities/identity payload emitted when the client connects.
	/// </summary>
	Hello,

	/// <summary>
	/// Window resize payload emitted when the client terminal dimensions change.
	/// </summary>
	Resize,
}
