namespace Repl;

/// <summary>
/// Represents one page of a larger result set.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public sealed class ReplPage<T> : IReplPage
{
	private object?[]? _untypedItems;

	/// <summary>
	/// Initializes a new instance of the <see cref="ReplPage{T}"/> class.
	/// </summary>
	/// <param name="items">Items in the page.</param>
	/// <param name="pageInfo">Page metadata.</param>
	public ReplPage(IReadOnlyList<T> items, ReplPageInfo pageInfo)
	{
		Items = items ?? throw new ArgumentNullException(nameof(items));
		PageInfo = pageInfo ?? throw new ArgumentNullException(nameof(pageInfo));
	}

	/// <summary>
	/// Gets the typed items in the page.
	/// </summary>
	public IReadOnlyList<T> Items { get; }

	/// <inheritdoc />
	public Type ItemType => typeof(T);

	/// <inheritdoc />
	public ReplPageInfo PageInfo { get; }

	/// <inheritdoc />
	public IReadOnlyList<object?> UntypedItems => _untypedItems ??= Items.Cast<object?>().ToArray();
}
