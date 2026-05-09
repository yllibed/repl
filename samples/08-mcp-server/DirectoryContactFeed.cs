using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Repl;

internal sealed record DirectoryContact(
	[property: Display(Name = "#", Order = 0)] int Id,
	[property: Display(Name = "Name", Order = 1)] string Name,
	[property: Display(Name = "Email", Order = 2)] string Email,
	[property: Display(Name = "Department", Order = 3)] string Department,
	[property: Display(Name = "Region", Order = 4)] string Region);

internal sealed class DirectoryContactFeed
{
	private readonly List<DirectoryContact> _items = CreateItems();

	public ReplPage<DirectoryContact> Query(IReplPagingContext paging)
	{
		ArgumentNullException.ThrowIfNull(paging);

		var offset = paging.AllRequested ? 0 : ParseOffset(paging.Cursor);
		var items = paging.AllRequested
			? _items
			: _items.Skip(offset).Take(paging.SuggestedPageSize).ToList();

		var nextOffset = offset + items.Count;
		var nextCursor = !paging.AllRequested && nextOffset < _items.Count
			? nextOffset.ToString(CultureInfo.InvariantCulture)
			: null;

		return paging.Page(items, nextCursor, _items.Count);
	}

	private static int ParseOffset(string? cursor) =>
		int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset > 0
			? offset
			: 0;

	private static List<DirectoryContact> CreateItems()
	{
		string[] firstNames = ["Alice", "Bob", "Charlie", "Diana", "Eve", "Frank", "Grace", "Heidi"];
		string[] lastNames = ["Martin", "Tremblay", "Singh", "Nguyen", "Roy", "Garcia", "Smith", "Brown"];
		string[] departments = ["Engineering", "Sales", "Support", "Marketing", "Finance", "Operations"];
		string[] regions = ["NA", "EMEA", "APAC", "LATAM"];

		return Enumerable.Range(1, 500)
			.Select(i =>
			{
				var firstName = firstNames[(i - 1) % firstNames.Length];
				var lastName = lastNames[((i - 1) / firstNames.Length) % lastNames.Length];

				return new DirectoryContact(
					i,
					$"{firstName} {lastName} {i:000}",
					$"{firstName.ToLowerInvariant()}.{lastName.ToLowerInvariant()}{i:000}@example.com",
					departments[(i - 1) % departments.Length],
					regions[(i - 1) % regions.Length]);
			})
			.ToList();
	}
}
