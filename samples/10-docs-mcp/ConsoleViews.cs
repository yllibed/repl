using System.Globalization;
using DocsMcpSample.Search;
using Repl;
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

	internal sealed class Output(IOutputTransformer fallback, Func<IAnsiConsole> resolveConsole) : IOutputTransformer
	{
		public string Name => fallback.Name;
		public bool SupportsInteractivePaging => true;
		public async ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default)
		{
			if (value is not (SearchComparison or IndexStatus or FetchedDocument)) { return await fallback.TransformAsync(value, cancellationToken); }
			cancellationToken.ThrowIfCancellationRequested();
			var session = resolveConsole();
			using var writer = new StringWriter(CultureInfo.InvariantCulture);
			var console = AnsiConsole.Create(new AnsiConsoleSettings
			{
				Out = new AnsiConsoleOutput(writer),
				Ansi = session.Profile.Capabilities.Ansi ? AnsiSupport.Yes : AnsiSupport.No,
				ColorSystem = session.Profile.Capabilities.Ansi ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors,
				Interactive = InteractionSupport.No,
				Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
			});
			console.Profile.Width = Math.Clamp(session.Profile.Width, 20, 240);
			console.Profile.Capabilities.Unicode = session.Profile.Capabilities.Unicode;
			if (value is IndexStatus status) { console.Write(Status(status)); }
			else if (value is SearchComparison comparison)
			{
				var rankings = new[] { Ranking("Keyword", comparison.Keyword), Ranking("Vector", comparison.Vector), Ranking("Hybrid · RRF", comparison.Hybrid) };
				console.Write(console.Profile.Width >= 110 ? new Columns(rankings) : new Rows(rankings));
			}
			else if (value is FetchedDocument document)
			{
				console.Write(new Rule(Markup.Escape(document.Source)));
				console.Write(new Text(document.Text));
				if (document.Truncated) { console.Write(new Text("\nTruncated; fetch a section for a focused extract.", new Style(Color.Yellow))); }
			}
			return writer.ToString();
		}
	}
}
