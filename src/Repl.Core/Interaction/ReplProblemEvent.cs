namespace Repl.Interaction;

/// <summary>
/// Semantic problem feedback intended for the current user.
/// </summary>
public sealed record ReplProblemEvent(
	string Summary,
	string? Details = null,
	string? Code = null)
	: ReplInteractionEvent(DateTimeOffset.UtcNow);
