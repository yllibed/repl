namespace Repl;

internal sealed record ResultFlowPagerOptions
{
	public int VisibleRows { get; init; }

	public Func<int>? VisibleRowsProvider { get; init; }

	public ReplPagerMode PagerMode { get; init; } = ReplPagerMode.More;

	public bool AnsiEnabled { get; init; }

	public bool HasMorePayload { get; init; }

	public Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? FetchNextPayload { get; init; }

	public IEnumerable<IReplPagerRenderer>? PagerRenderers { get; init; }

	public int MaxBufferedLines { get; init; } = ResultFlowOptions.DefaultMaxBufferedLines;
}
