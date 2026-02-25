namespace Repl;

/// <summary>
/// Represents a completion provider for a route target.
/// </summary>
/// <param name="context">Completion context.</param>
/// <param name="input">Current user input.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Completion candidates.</returns>
public delegate ValueTask<IReadOnlyList<string>> CompletionDelegate(
	CompletionContext context,
	string input,
	CancellationToken cancellationToken);
