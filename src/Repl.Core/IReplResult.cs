namespace Repl;

/// <summary>
/// Represents an explicit result produced by command handlers.
/// </summary>
public interface IReplResult
{
	/// <summary>
	/// Gets the high-level result kind.
	/// </summary>
	string Kind { get; }

	/// <summary>
	/// Gets a machine-readable error code when applicable.
	/// </summary>
	string? Code { get; }

	/// <summary>
	/// Gets a user-facing result message.
	/// </summary>
	string Message { get; }

	/// <summary>
	/// Gets optional structured details.
	/// </summary>
	object? Details { get; }
}