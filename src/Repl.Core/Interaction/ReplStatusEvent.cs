namespace Repl.Interaction;

/// <summary>
/// Semantic status line event.
/// </summary>
public sealed record ReplStatusEvent(string Text)
	: ReplInteractionEvent(DateTimeOffset.UtcNow);
