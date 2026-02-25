namespace Repl.Testing;

/// <summary>
/// Interaction event observed from the REPL runtime.
/// </summary>
public sealed record InteractionObservedEvent(ReplInteractionEvent Interaction)
	: CommandEvent(DateTimeOffset.UtcNow);
