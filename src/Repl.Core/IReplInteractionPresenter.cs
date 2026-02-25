namespace Repl;

/// <summary>
/// Renders semantic interaction events to an output target.
/// </summary>
public interface IReplInteractionPresenter
{
	/// <summary>
	/// Presents one semantic event.
	/// </summary>
	/// <param name="evt">Semantic interaction event.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>An asynchronous operation.</returns>
	ValueTask PresentAsync(
		ReplInteractionEvent evt,
		CancellationToken cancellationToken);
}
