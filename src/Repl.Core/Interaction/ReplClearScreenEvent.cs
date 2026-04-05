namespace Repl.Interaction;

/// <summary>
/// Semantic event requesting that the terminal screen be cleared.
/// </summary>
public sealed record ReplClearScreenEvent() : ReplInteractionEvent(DateTimeOffset.UtcNow);
