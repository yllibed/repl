namespace Repl;

internal sealed class ReplPageDisplaySnapshot : IReplPage
{
	private readonly IReplPage _page;

	public ReplPageDisplaySnapshot(IReplPage page, ReplPageInfo pageInfo)
	{
		_page = page ?? throw new ArgumentNullException(nameof(page));
		PageInfo = pageInfo ?? throw new ArgumentNullException(nameof(pageInfo));
	}

	public Type ItemType => _page.ItemType;

	public ReplPageInfo PageInfo { get; }

	public IReadOnlyList<object?> UntypedItems => _page.UntypedItems;
}
