using System.Text.Json.Serialization;

namespace Repl;

/// <summary>
/// Represents one page of a larger result set.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public sealed record ReplPage<T> : IReplPage
{
	private IReadOnlyList<object?>? _untypedItems;

	/// <summary>
	/// Initializes a new instance of the <see cref="ReplPage{T}"/> record.
	/// </summary>
	/// <param name="items">Items in the page.</param>
	/// <param name="pageInfo">Page metadata.</param>
	public ReplPage(IReadOnlyList<T> items, ReplPageInfo pageInfo)
	{
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(pageInfo);

		Items = items;
		PageInfo = pageInfo;
	}

	/// <summary>
	/// Gets the typed items in the page.
	/// </summary>
	public IReadOnlyList<T> Items { get; init; }

	/// <inheritdoc />
	[JsonIgnore]
	public Type ItemType => typeof(T);

	/// <inheritdoc />
	public ReplPageInfo PageInfo { get; init; }

	/// <inheritdoc />
	[JsonIgnore]
	public IReadOnlyList<object?> UntypedItems => _untypedItems ??= Items switch
	{
		object?[] array => array,
		IReadOnlyList<object?> list => list,
		_ => Items.Cast<object?>().ToArray(),
	};
}
