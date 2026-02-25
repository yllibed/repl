namespace Repl;

/// <summary>
/// Semantic progress event.
/// </summary>
public sealed record ReplProgressEvent(
	string Label,
	double? Percent = null,
	int? Current = null,
	int? Total = null,
	string? Unit = null)
	: ReplInteractionEvent(DateTimeOffset.UtcNow)
{
	/// <summary>
	/// Computes a percentage when not provided explicitly and current/total are available.
	/// </summary>
	public double? ResolvePercent() =>
		Percent ?? (Current is > 0 && Total is > 0
			? (double)Current.Value / Total.Value * 100d
			: null);
}
