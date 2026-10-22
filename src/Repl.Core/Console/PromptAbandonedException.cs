namespace Repl;

/// <summary>
/// A secret prompt ended by its user or its terminal, through Esc or a terminal stream that ended, rather than by its
/// deadline.
/// </summary>
/// <remarks>
/// Callers still see an <see cref="OperationCanceledException"/>. A timed prompt tells this apart from its own timer by
/// type, because the timer may also have fired by the time the exception is caught.
/// Repl.Defaults constructs it (<c>VtKeyReader</c>), so its name, namespace and constructor are friend-assembly ABI.
/// </remarks>
internal sealed class PromptAbandonedException(string message, CancellationToken token, Exception? innerException = null)
	: OperationCanceledException(message, innerException, token);
