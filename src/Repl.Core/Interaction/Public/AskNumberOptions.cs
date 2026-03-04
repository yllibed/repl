namespace Repl.Interaction;

/// <summary>
/// Options for <see cref="ReplInteractionChannelExtensions.AskNumberAsync{T}"/>.
/// </summary>
/// <typeparam name="T">The numeric type.</typeparam>
/// <param name="CancellationToken">
/// Explicit cancellation token. When <c>default</c>, the channel uses the
/// ambient per-command token set by the framework before each command dispatch.
/// </param>
/// <param name="Timeout">
/// Optional timeout for the prompt.
/// </param>
/// <param name="Min">Optional minimum bound (inclusive).</param>
/// <param name="Max">Optional maximum bound (inclusive).</param>
public record AskNumberOptions<T>(
	CancellationToken CancellationToken = default,
	TimeSpan? Timeout = null,
	T? Min = null,
	T? Max = null) where T : struct;
