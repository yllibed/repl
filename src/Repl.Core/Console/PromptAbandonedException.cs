namespace Repl;

/// <summary>
/// A prompt ended by Esc or by the end of its terminal input, rather than by its deadline.
/// </summary>
/// <remarks>
/// Callers still see an <see cref="OperationCanceledException"/>; a timed prompt never reads this one as a timeout.
/// Repl.Defaults constructs it (<c>VtKeyReader</c>), so its name, namespace and constructor are friend-assembly ABI.
/// </remarks>
internal sealed class PromptAbandonedException(string message, CancellationToken token, Exception? innerException = null)
	: OperationCanceledException(message, innerException, token);
