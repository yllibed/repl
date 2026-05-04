namespace Repl;

/// <summary>
/// Configures Repl result-flow behavior for paging and large result sets.
/// </summary>
public sealed class ResultFlowOptions
{
	/// <summary>
	/// Gets or sets the default page size when no terminal-specific hint is available.
	/// </summary>
	public int DefaultPageSize { get; set; } = 100;

	/// <summary>
	/// Gets or sets the maximum page size a caller can request.
	/// </summary>
	public int MaxPageSize { get; set; } = 1000;

	/// <summary>
	/// Gets or sets the number of non-data rows reserved in interactive pagers.
	/// </summary>
	public int ReservedVisibleRows { get; set; } = 2;

	/// <summary>
	/// Gets or sets the default pager mode for human output.
	/// </summary>
	public ReplPagerMode DefaultPagerMode { get; set; } = ReplPagerMode.Auto;

	/// <summary>
	/// Gets or sets the maximum inline payload size for programmatic clients.
	/// </summary>
	public int ProgrammaticMaxInlineBytes { get; set; } = 64 * 1024;
}
