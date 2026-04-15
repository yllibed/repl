namespace Repl.Interaction;

/// <summary>
/// Semantic warning feedback intended for the current user.
/// </summary>
public sealed record ReplWarningEvent(string Text)
	: ReplInteractionEvent(DateTimeOffset.UtcNow);
