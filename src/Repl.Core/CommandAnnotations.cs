namespace Repl;

/// <summary>
/// Structured behavioral annotations for a command.
/// Used by both human-facing surfaces (<c>--help</c>) and programmatic surfaces (MCP tool registration).
/// </summary>
public sealed record CommandAnnotations
{
	/// <summary>
	/// Indicates the command modifies state (deletes, updates, etc.).
	/// When exposed to agents, triggers a confirmation prompt before execution.
	/// </summary>
	public bool Destructive { get; init; }

	/// <summary>
	/// Indicates the command only reads data without side effects.
	/// ReadOnly commands are auto-promoted to MCP resources.
	/// </summary>
	public bool ReadOnly { get; init; }

	/// <summary>
	/// Indicates the command can be called multiple times with the same result.
	/// Agents may safely retry idempotent commands on transient failure.
	/// </summary>
	public bool Idempotent { get; init; }

	/// <summary>
	/// Indicates the command interacts with external systems beyond the app.
	/// Helps agents anticipate latency and failure modes.
	/// </summary>
	public bool OpenWorld { get; init; }

	/// <summary>
	/// Indicates the command may take a long time to complete.
	/// Enables task-based execution in programmatic clients.
	/// </summary>
	public bool LongRunning { get; init; }

	/// <summary>
	/// Hides the command from programmatic/automation surfaces.
	/// Unlike <see cref="CommandBuilder.Hidden(bool)"/>, which hides from all surfaces,
	/// this only suppresses programmatic discovery (e.g. MCP tool registration).
	/// The command remains visible in interactive help and REPL.
	/// </summary>
	public bool AutomationHidden { get; init; }
}
