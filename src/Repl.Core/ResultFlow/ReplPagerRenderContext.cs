namespace Repl;

/// <summary>
/// Provides terminal, input, and payload state to a custom result-flow pager renderer.
/// </summary>
public sealed class ReplPagerRenderContext
{
	private readonly Func<CancellationToken, ValueTask<ReplPagerPayload?>>? _fetchNextPayload;

	/// <summary>
	/// Initializes a new instance of the <see cref="ReplPagerRenderContext"/> class.
	/// </summary>
	public ReplPagerRenderContext(
		string initialPayload,
		TextWriter output,
		IReplKeyReader keyReader,
		int visibleRows,
		Func<int>? visibleRowsProvider,
		bool ansiEnabled,
		bool hasMorePayload,
		Func<CancellationToken, ValueTask<ReplPagerPayload?>>? fetchNextPayload)
	{
		ArgumentNullException.ThrowIfNull(initialPayload);
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(keyReader);

		InitialPayload = initialPayload;
		Output = output;
		KeyReader = keyReader;
		VisibleRows = visibleRows;
		VisibleRowsProvider = visibleRowsProvider;
		AnsiEnabled = ansiEnabled;
		HasMorePayload = hasMorePayload;
		_fetchNextPayload = fetchNextPayload;
	}

	/// <summary>
	/// Gets the first rendered payload.
	/// </summary>
	public string InitialPayload { get; }

	/// <summary>
	/// Gets the output writer controlled by the pager.
	/// </summary>
	public TextWriter Output { get; }

	/// <summary>
	/// Gets the key reader used for interactive navigation.
	/// </summary>
	public IReplKeyReader KeyReader { get; }

	/// <summary>
	/// Gets the initial visible row count available to the pager.
	/// </summary>
	public int VisibleRows { get; }

	/// <summary>
	/// Gets the current visible row resolver when available.
	/// </summary>
	public Func<int>? VisibleRowsProvider { get; }

	/// <summary>
	/// Gets whether ANSI terminal control sequences can be used.
	/// </summary>
	public bool AnsiEnabled { get; }

	/// <summary>
	/// Gets whether another payload can initially be fetched.
	/// </summary>
	public bool HasMorePayload { get; }

	/// <summary>
	/// Gets a value indicating whether a next payload fetcher is available.
	/// </summary>
	public bool CanFetchNextPayload => _fetchNextPayload is not null;

	/// <summary>
	/// Fetches the next rendered payload when the result source can continue.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The next payload, or null when no fetcher is available or the source ended.</returns>
	public ValueTask<ReplPagerPayload?> FetchNextPayloadAsync(CancellationToken cancellationToken = default) =>
		_fetchNextPayload is null
			? ValueTask.FromResult<ReplPagerPayload?>(null)
			: _fetchNextPayload(cancellationToken);
}
