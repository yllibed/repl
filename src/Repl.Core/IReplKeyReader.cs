namespace Repl;

/// <summary>
/// Injectable service for handlers that need raw key input (watch/top pattern).
/// When a handler declares this parameter, it owns the console input and decides
/// what each key means.
/// </summary>
public interface IReplKeyReader
{
	/// <summary>
	/// Reads the next key press. Blocks until a key is available or token is cancelled.
	/// </summary>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The key info of the pressed key.</returns>
	ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct);

	/// <summary>
	/// Returns true if a key is available to read without blocking.
	/// </summary>
	bool KeyAvailable { get; }
}
