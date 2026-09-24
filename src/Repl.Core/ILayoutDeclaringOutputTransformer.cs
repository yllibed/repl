namespace Repl;

/// <summary>
/// A human output transformer that declares the layout of what it renders, so the pager can pin its header, drop
/// that header's repeats and strip its footer without inferring any of them from the text.
/// </summary>
/// <remarks>
/// A table always renders its header, on every page: a page of JSON rows takes its columns from its own keys, so it
/// can name other columns than the page before it. The pager drops a header that repeats the previous page's
/// columns and keeps one that names others. This is a separate interface rather than new members of
/// <see cref="IResultFlowOutputTransformer"/>: a friend assembly built against an earlier Repl.Core implements that
/// one as it shipped, and still has to load when only Repl.Core is upgraded.
/// </remarks>
internal interface ILayoutDeclaringOutputTransformer : IOutputTransformer
{
	/// <summary>Renders <paramref name="value"/> as <see cref="IOutputTransformer.TransformAsync"/> does, with its layout.</summary>
	ValueTask<RenderedPayload> RenderAsync(object? value, CancellationToken cancellationToken = default);

	/// <summary>Renders a page fetched for the pager: its items, without the footer that asks to rerun for more.</summary>
	ValueTask<RenderedPayload> RenderPageAsync(IReplPage page, CancellationToken cancellationToken = default);
}
