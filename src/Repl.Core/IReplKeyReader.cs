namespace Repl;

/// <summary>
/// Injectable service for handlers that need raw key input (watch/top pattern).
/// When a handler declares this parameter, it owns the console input and decides
/// what each key means.
/// Custom <see cref="IReplPagerRenderer"/> implementations can also use this
/// service through <see cref="ReplPagerRenderContext"/> to drive interactive
/// navigation without depending directly on <see cref="Console"/>.
/// </summary>
public interface IReplKeyReader
{
	/// <summary>
	/// Reads the next key press. Blocks until a key is available or token is cancelled.
	/// </summary>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The key info of the pressed key.</returns>
	/// <exception cref="OperationCanceledException">
	/// <paramref name="ct"/> was cancelled: the exception carries <paramref name="ct"/> or a token linked to it, so a timed
	/// prompt can tell its deadline apart. Or the input ended: the exception carries no cancelled token, and a prompt
	/// fails instead of timing out.
	/// </exception>
	ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct);

	/// <summary>
	/// Returns true if a key is available to read without blocking.
	/// </summary>
	bool KeyAvailable { get; }
}
