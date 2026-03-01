namespace Repl.Interaction;

/// <summary>
/// Base semantic interaction event emitted during command execution.
/// </summary>
public abstract record ReplInteractionEvent(DateTimeOffset Timestamp);
