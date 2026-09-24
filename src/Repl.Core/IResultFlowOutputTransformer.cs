namespace Repl;

// Kept as it shipped in 0.11, with ResultFlowPageRenderMode: a friend assembly built against it, such as an older
// Repl.Spectre, implements this exact signature and must still load when only Repl.Core is upgraded. Transformers
// built with this version declare their layout through ILayoutDeclaringOutputTransformer instead.
internal interface IResultFlowOutputTransformer : IOutputTransformer
{
	ValueTask<string> TransformPageAsync(
		IReplPage page,
		ResultFlowPageRenderMode mode,
		CancellationToken cancellationToken = default);
}
