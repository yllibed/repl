namespace Repl;

/// <summary>
/// Defines a custom ambient command available in all interactive scopes.
/// </summary>
internal sealed class AmbientCommandDefinition
{
	/// <summary>
	/// Gets the command name (matched case-insensitively).
	/// </summary>
	public required string Name { get; init; }

	/// <summary>
	/// Gets the optional description shown in help output.
	/// </summary>
	public string? Description { get; init; }

	/// <summary>
	/// Gets the handler delegate. Parameters are injected using the same
	/// binding rules as regular command handlers (DI services,
	/// <see cref="IReplInteractionChannel"/>, <see cref="CancellationToken"/>, etc.).
	/// </summary>
	public required Delegate Handler { get; init; }
}
