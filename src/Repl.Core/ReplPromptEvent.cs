namespace Repl;

/// <summary>
/// Semantic prompt event.
/// </summary>
public sealed record ReplPromptEvent(string Name, string PromptText, string Kind)
	: ReplInteractionEvent(DateTimeOffset.UtcNow);
