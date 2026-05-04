namespace Repl;

/// <summary>
/// Describes the output surface used for a command result.
/// </summary>
public enum ReplResultSurface
{
	/// <summary>
	/// A local console or terminal.
	/// </summary>
	Console,

	/// <summary>
	/// An interactive REPL session.
	/// </summary>
	Interactive,

	/// <summary>
	/// Standard output is redirected to a pipe or file.
	/// </summary>
	Redirected,

	/// <summary>
	/// A hosted terminal session is active.
	/// </summary>
	Hosted,

	/// <summary>
	/// A programmatic client, such as MCP, is driving execution.
	/// </summary>
	Programmatic,
}
