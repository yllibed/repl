namespace Repl;

/// <summary>
/// Configures Repl result-flow behavior for paging and large result sets.
/// </summary>
public sealed class ResultFlowOptions
{
	private readonly List<IReplPagerRenderer> _pagerRenderers = [];
	private int _defaultPageSize = 100;
	private int _maxPageSize = 1000;

	/// <summary>
	/// Gets or sets the default page size when no terminal-specific hint is available.
	/// </summary>
	public int DefaultPageSize
	{
		get => _defaultPageSize;
		set
		{
			if (value < 1)
			{
				throw new ArgumentOutOfRangeException(nameof(value), "Default page size must be greater than zero.");
			}

			_defaultPageSize = value;
			if (_maxPageSize < _defaultPageSize)
			{
				_maxPageSize = _defaultPageSize;
			}
		}
	}

	/// <summary>
	/// Gets or sets the maximum page size a caller can request.
	/// </summary>
	public int MaxPageSize
	{
		get => _maxPageSize;
		set
		{
			if (value < 1)
			{
				throw new ArgumentOutOfRangeException(nameof(value), "Maximum page size must be greater than zero.");
			}

			if (value < _defaultPageSize)
			{
				throw new ArgumentOutOfRangeException(nameof(value), "Maximum page size must be greater than or equal to the default page size.");
			}

			_maxPageSize = value;
		}
	}

	/// <summary>
	/// Gets or sets the number of non-data rows reserved in interactive pagers.
	/// </summary>
	public int ReservedVisibleRows { get; set; } = 2;

	/// <summary>
	/// Gets or sets the default pager mode for human output.
	/// </summary>
	public ReplPagerMode DefaultPagerMode { get; set; } = ReplPagerMode.Auto;

	/// <summary>
	/// Gets custom pager renderers keyed by <see cref="IReplPagerRenderer.Mode"/>.
	/// </summary>
	public IReadOnlyList<IReplPagerRenderer> PagerRenderers => _pagerRenderers;

	/// <summary>
	/// Gets or sets the maximum number of content lines an interactive pager buffers in memory.
	/// </summary>
	public int MaxBufferedLines { get; set; } = 10_000;

	/// <summary>
	/// Gets or sets the maximum inline payload size for programmatic clients.
	/// </summary>
	public int ProgrammaticMaxInlineBytes { get; set; } = 64 * 1024;

	/// <summary>
	/// Registers or replaces the pager renderer for its configured mode.
	/// </summary>
	/// <param name="renderer">Renderer to register.</param>
	/// <returns>This options instance.</returns>
	public ResultFlowOptions UsePagerRenderer(IReplPagerRenderer renderer)
	{
		ArgumentNullException.ThrowIfNull(renderer);
		_ = RemovePagerRenderer(renderer.Mode);
		_pagerRenderers.Add(renderer);
		return this;
	}

	/// <summary>
	/// Removes the custom pager renderer registered for a mode.
	/// </summary>
	/// <param name="mode">Pager mode to remove.</param>
	/// <returns>True when a renderer was removed.</returns>
	public bool RemovePagerRenderer(ReplPagerMode mode) =>
		_pagerRenderers.RemoveAll(renderer => renderer.Mode == mode) > 0;

	/// <summary>
	/// Removes all custom pager renderers.
	/// </summary>
	public void ClearPagerRenderers() =>
		_pagerRenderers.Clear();
}
