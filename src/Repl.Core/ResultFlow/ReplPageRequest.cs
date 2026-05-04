namespace Repl;

/// <summary>
/// Request sent to a page source.
/// </summary>
/// <param name="PageSize">Requested page size.</param>
/// <param name="Cursor">Opaque cursor for continuation.</param>
/// <param name="VisibleRowCapacityHint">Best-effort visible row capacity for the output surface.</param>
/// <param name="AllRequested">Whether the caller requested all available rows.</param>
/// <param name="Surface">Output surface requesting the page.</param>
public sealed record ReplPageRequest(
	int PageSize,
	string? Cursor,
	int? VisibleRowCapacityHint,
	bool AllRequested,
	ReplResultSurface Surface);
