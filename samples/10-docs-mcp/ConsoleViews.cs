using DocsMcpSample.Search;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DocsMcpSample;

internal static class ConsoleViews
{
	internal static IRenderable Status(IndexStatus status)
	{
		var table = new Table().Border(TableBorder.Rounded).AddColumn("Index").AddColumn("State");
		table.AddRow("Serving", status.Ready ? "Ready" : "Waiting for the first index");
		table.AddRow(new Text("Generation"), new Text(status.Generation?.Id ?? "—"));
		table.AddRow("Published corpus", $"{status.Generation?.Documents ?? 0} documents / {status.Generation?.Sections ?? 0} sections");
		table.AddRow(new Text(status.Phase), new Text(status.Rebuilding ? $"{status.DocumentsProcessed} documents / {status.SectionsProcessed} sections prepared" : ""));
		if (status.LastError is { } error) { table.AddRow(new Text("Last failure", new Style(Color.Red)), new Text(error)); }
		if (status.NextRetryAt is { } retry) { table.AddRow(new Text("Automatic retry"), new Text(retry.ToString("u"))); }
		return table;
	}

	private static Table Ranking(string title, IReadOnlyList<SearchHit> hits)
	{
		var table = new Table().Border(TableBorder.Simple).AddColumn(title);
		for (var rank = 0; rank < hits.Count; rank++)
		{
			var hit = hits[rank];
			table.AddRow(new Rows(new Text($"{rank + 1}. {hit.Title}", new Style(Color.SkyBlue1)), new Text(hit.Heading),
				new Text($"{hit.Source}:{hit.StartLine}", new Style(Color.Grey)), new Text(hit.Id)));
		}
		if (hits.Count == 0) { table.AddRow("No matches"); }
		return table;
	}

	internal static IRenderable Comparison(SearchComparison comparison) => new Columns(
		Ranking("Keyword", comparison.Keyword),
		Ranking("Vector", comparison.Vector),
		Ranking("Hybrid · RRF", comparison.Hybrid));

	internal static IRenderable Document(FetchedDocument document)
	{
		var sections = new List<IRenderable>
		{
			new Rule(Markup.Escape(document.Source)),
			new Text(document.Text),
		};
		if (document.Truncated)
		{
			sections.Add(new Text("\nTruncated; fetch a section for a focused extract.", new Style(Color.Yellow)));
		}
		return new Rows(sections);
	}
}
