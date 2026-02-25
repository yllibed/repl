namespace Repl.Testing;

/// <summary>
/// Raw result object emitted by command execution.
/// </summary>
public sealed record ResultProducedEvent(object? Result)
	: CommandEvent(DateTimeOffset.UtcNow);
