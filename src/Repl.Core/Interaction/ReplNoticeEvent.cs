namespace Repl.Interaction;

/// <summary>
/// Semantic informational feedback intended for the current user.
/// </summary>
public sealed record ReplNoticeEvent(string Text)
	: ReplInteractionEvent(DateTimeOffset.UtcNow);
