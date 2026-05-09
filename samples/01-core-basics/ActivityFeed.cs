using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Repl;

sealed record ActivityEvent(
	[property: Display(Name = "#", Order = 0)] int Id,
	[property: Display(Name = "At", Order = 1)] string At,
	[property: Display(Name = "Area", Order = 2)] string Area,
	[property: Display(Name = "Event", Order = 3)] string Event,
	[property: Display(Name = "Summary", Order = 4)] string Summary);

internal sealed class ActivityFeed
{
	private readonly List<ActivityEvent> _items = CreateItems();

	public IReplPageSource<ActivityEvent> Query(IReplPagingContext paging)
	{
		ArgumentNullException.ThrowIfNull(paging);

		return ReplPageSource.FromOffset<ActivityEvent>(
			(offset, take, _) =>
				ValueTask.FromResult<IReadOnlyList<ActivityEvent>>(
					_items.Skip(offset).Take(take).ToList()),
			_items.Count);
	}

	private static List<ActivityEvent> CreateItems()
	{
		string[] areas = ["identity", "billing", "catalog", "search", "import", "reporting"];
		string[] events = ["validated", "queued", "indexed", "exported", "reconciled", "notified"];
		var start = new DateTimeOffset(2026, 1, 12, 8, 0, 0, TimeSpan.Zero);

		return Enumerable.Range(1, 250)
			.Select(i =>
			{
				var area = areas[(i - 1) % areas.Length];
				var eventName = events[(i - 1) % events.Length];

				return new ActivityEvent(
					i,
					start.AddMinutes(i * 7d).ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture),
					area,
					eventName,
					$"{area} batch {((i - 1) / 5) + 1} {eventName} successfully");
			})
			.ToList();
	}
}
