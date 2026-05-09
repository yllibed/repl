namespace Repl;

internal sealed class ReplPagingContext : IReplPagingContext
{
	public ReplPagingContext(
		ResultFlowOptions options,
		ResultFlowInvocationOptions invocation,
		ReplResultSurface surface,
		int? visibleRowCapacityHint)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(invocation);

		MaxPageSize = Math.Max(1, options.MaxPageSize);
		VisibleRowCapacityHint = visibleRowCapacityHint;
		Cursor = invocation.Cursor;
		AllRequested = invocation.AllRequested;
		Surface = surface;
		SuggestedPageSize = ClampPageSize(
			invocation.PageSize
				?? visibleRowCapacityHint
				?? options.DefaultPageSize,
			MaxPageSize);
	}

	public int? VisibleRowCapacityHint { get; }

	public int SuggestedPageSize { get; }

	public int MaxPageSize { get; }

	public string? Cursor { get; }

	public bool AllRequested { get; }

	public ReplResultSurface Surface { get; }

	internal ReplPageRequest CreateRequest() =>
		new(SuggestedPageSize, Cursor, VisibleRowCapacityHint, AllRequested, Surface);

	private static int ClampPageSize(int value, int maxPageSize) =>
		Math.Clamp(value, 1, maxPageSize);
}
