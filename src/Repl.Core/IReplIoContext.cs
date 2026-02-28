namespace Repl;

/// <summary>
/// Exposes low-level runtime I/O streams for command handlers.
/// </summary>
public interface IReplIoContext
{
	/// <summary>
	/// Gets the active input reader.
	/// </summary>
	TextReader Input { get; }

	/// <summary>
	/// Gets the active output writer.
	/// </summary>
	TextWriter Output { get; }

	/// <summary>
	/// Gets the active error writer.
	/// </summary>
	TextWriter Error { get; }

	/// <summary>
	/// Gets a value indicating whether execution is currently running in a real hosted transport session.
	/// This is false for local CLI execution, including protocol passthrough scopes.
	/// </summary>
	bool IsHostedSession { get; }

	/// <summary>
	/// Gets the current hosted session identifier, when available.
	/// </summary>
	string? SessionId { get; }
}
