namespace Repl.Testing;

/// <summary>
/// Output text chunk emitted by the host.
/// </summary>
public sealed record OutputWrittenEvent(string Text)
	: CommandEvent(DateTimeOffset.UtcNow);
