namespace Repl;

internal interface IResultFlowOutputTransformer : IOutputTransformer
{
	ValueTask<string> TransformPageAsync(
		IReplPage page,
		ResultFlowPageRenderMode mode,
		CancellationToken cancellationToken = default);
}
