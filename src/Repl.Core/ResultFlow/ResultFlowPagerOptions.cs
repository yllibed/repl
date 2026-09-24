namespace Repl;

internal sealed record ResultFlowPagerOptions
{
	public int VisibleRows { get; init; }

	public Func<int>? VisibleRowsProvider { get; init; }

	public ReplPagerMode PagerMode { get; init; } = ReplPagerMode.More;

	public bool AnsiEnabled { get; init; }

	public bool HasMorePayload { get; init; }

	/// <summary>
	/// The layout the first payload's transformer declared, or <see langword="null"/> to detect its header and
	/// footer from the text. It decides for the whole session: fetched payloads come from the same transformer.
	/// </summary>
	public RenderedLayout? PayloadLayout { get; init; }

	public Func<CancellationToken, ValueTask<ResultFlowPagerPage?>>? FetchNextPayload { get; init; }

	public IReadOnlyList<IReplPagerRenderer>? PagerRenderers { get; init; }

	public int MaxBufferedLines { get; init; } = ResultFlowOptions.DefaultMaxBufferedLines;
}
