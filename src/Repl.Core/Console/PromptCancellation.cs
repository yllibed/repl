namespace Repl;

/// <summary>
/// Attributes a cancellation to a prompt's token at the operation that was handed that token.
/// </summary>
/// <remarks>
/// Only an operation handed the prompt's token, such as reading a key or flushing an echo, can be cancelled by its
/// deadline, and it may observe that token through one it linked to its own lifetime. Whether two tokens are linked
/// cannot be observed, so a cancelled token of the operation's own, while the prompt's token is cancelled too, is taken
/// as the prompt's: an independent cancellation at that exact moment is indistinguishable. Reported as the prompt
/// token's own, it lets a timed prompt recognise its deadline by token alone.
/// </remarks>
internal static class PromptCancellation
{
	/// <summary>
	/// Whether <paramref name="exception"/>, from an operation handed <paramref name="ct"/>, carries another cancelled
	/// token while <paramref name="ct"/> is cancelled.
	/// </summary>
	public static bool IsThroughAnotherToken(OperationCanceledException exception, CancellationToken ct) =>
		exception.CancellationToken != ct
		&& ct.IsCancellationRequested
		&& exception.CancellationToken.IsCancellationRequested;

	/// <summary>
	/// The same cancellation, reported as <paramref name="ct"/>'s own.
	/// </summary>
	public static OperationCanceledException AsCancellationOf(OperationCanceledException exception, CancellationToken ct) =>
		new(exception.Message, exception, ct);
}
