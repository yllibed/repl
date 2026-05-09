namespace Repl;

/// <summary>
/// Renders result-flow payloads in an interactive human terminal pager.
/// </summary>
public interface IReplPagerRenderer
{
	/// <summary>
	/// Gets the pager mode handled by this renderer.
	/// </summary>
	ReplPagerMode Mode { get; }

	/// <summary>
	/// Renders the pager session.
	/// </summary>
	/// <param name="context">Pager render context.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>A value task that completes when the pager session exits.</returns>
	ValueTask RenderAsync(ReplPagerRenderContext context, CancellationToken cancellationToken = default);
}
