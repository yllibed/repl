namespace Repl;

/// <summary>
/// Represents a typed page using an untyped view for the output pipeline.
/// </summary>
/// <remarks>
/// Prefer returning <see cref="ReplPage{T}"/> or one of the page-source helpers instead of implementing
/// this interface directly; it is primarily a framework contract between result-flow components.
/// </remarks>
public interface IReplPage
{
	/// <summary>
	/// Gets the runtime item type declared by the page.
	/// </summary>
	Type ItemType { get; }

	/// <summary>
	/// Gets page metadata.
	/// </summary>
	ReplPageInfo PageInfo { get; }

	/// <summary>
	/// Gets the current page items as an untyped list.
	/// </summary>
	IReadOnlyList<object?> UntypedItems { get; }
}
