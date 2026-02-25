namespace Repl;

/// <summary>
/// Extensible options for Ask prompts. Groups <see cref="CancellationToken"/>
/// and <see cref="Timeout"/> (and future features) in one place to avoid
/// signature churn on <see cref="IReplInteractionChannel"/> methods.
/// </summary>
/// <param name="CancellationToken">
/// Explicit cancellation token. When <c>default</c>, the channel uses the
/// ambient per-command token set by the framework before each command dispatch.
/// </param>
/// <param name="Timeout">
/// Optional timeout for the prompt. When the timeout elapses, the default
/// value is auto-selected and a countdown is displayed inline.
/// </param>
public record AskOptions(
	CancellationToken CancellationToken = default,
	TimeSpan? Timeout = null);
