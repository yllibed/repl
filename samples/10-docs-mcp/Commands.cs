using System.ComponentModel;
using System.Runtime.CompilerServices;
using DocsMcpSample.Hosting;
using DocsMcpSample.Search;
using Repl;
using Repl.Parameters;
using Repl.Spectre;
using Spectre.Console;
using Results = Repl.Results;

namespace DocsMcpSample;

internal static class Commands
{
	internal static void Map(ReplApp repl)
	{
		repl.Map("docs search", (string query, DocumentIndex index, IndexingWorker worker, CancellationToken ct, [ReplOption] int count = 5) =>
		{
			worker.RequireReady();
			return index.Search(query, count, ct).Hybrid;
		})
			.WithDescription("Search the Markdown documentation by keywords and meaning. Fetch an identifier from the results to read its source.").ReadOnly();
		repl.Map("docs fetch", (string id, DocumentIndex index, IndexingWorker worker, [ReplOption] int maxChars = 20000) =>
		{
			worker.RequireReady();
			return index.Fetch(id, maxChars);
		})
			.WithDescription("Read an indexed document or section using an identifier returned by search. Reports when the text is truncated.").ReadOnly();
		repl.Map("docs list", (DocumentIndex index, [ReplOption] int offset = 0, [ReplOption] int count = 20) => index.List(offset, count))
			.WithDescription("List indexed documents; use --offset and --count to page through the corpus.").ReadOnly();
		repl.Map("search compare", (string query, DocumentIndex index, CancellationToken ct, [ReplOption] int count = 5) => index.Search(query, count, ct))
			.WithDescription("Compare keyword, vector and hybrid rankings for one query.").ReadOnly();
		repl.Map("index status", (IndexingWorker worker) => worker.Status).WithDescription("Show the published generation and current indexing activity.").ReadOnly();
		repl.Map("index rebuild", (IndexingWorker worker) => worker.RequestRebuild()).WithDescription("Queue a full rebuild; the current index remains available.");
		repl.Map("index cancel", async (IndexingWorker worker) => await worker.CancelRebuildAsync()).WithDescription("Cancel rebuilding without deleting the published index.");
		repl.Map("index watch", WatchAsync).WithDescription("Watch indexing live. Ctrl+C stops observing, not indexing.");
		repl.Map("sessions", (Sessions sessions) => sessions.List()).WithDescription("List browser sessions and local CLI connections.").ReadOnly();
		repl.Map("health", (IndexingWorker worker, IHostApplicationLifetime lifetime, [ReplOption] bool ready = false) =>
		{
			var alive = lifetime.ApplicationStarted.IsCancellationRequested && !lifetime.ApplicationStopping.IsCancellationRequested;
			return Results.Exit(alive && (!ready || worker.Status.Ready) ? 0 : 1, new { Alive = alive, Ready = worker.Status.Ready });
		}).WithDescription("Check process liveness; add --ready to require a usable index.").ReadOnly();
	}

	private static async Task WatchAsync(IndexingWorker worker, IAnsiConsole console, SpectreInteractionPresenter presenter, IReplIoContext io, IReplPagingContext paging, CancellationToken ct)
	{
		if (paging.Surface is ReplResultSurface.Programmatic or ReplResultSurface.Redirected) { throw new InvalidOperationException("Use index status for a one-shot snapshot, or attach a console to watch indexing."); }
		using var capture = presenter.BeginCapture(io.Error);
		if (!console.Profile.Capabilities.Ansi)
		{
			await foreach (var status in ObserveAsync(worker, ct))
			{
				console.Write(ConsoleViews.Status(status));
				await console.Profile.Out.Writer.FlushAsync(ct);
			}
			return;
		}
		console.Profile.Capabilities.Interactive = true;
		await console.Live(ConsoleViews.Status(worker.Status)).AutoClear(false).StartAsync(async display =>
		{
			await foreach (var status in ObserveAsync(worker, ct))
			{
				display.UpdateTarget(ConsoleViews.Status(status));
				display.Refresh();
				// Hosted writers buffer synchronous Spectre writes until explicitly flushed.
				await console.Profile.Out.Writer.FlushAsync(ct);
			}
		});
	}

	private static async IAsyncEnumerable<IndexStatus> ObserveAsync(IndexingWorker worker, [EnumeratorCancellation] CancellationToken ct)
	{
		IndexStatus? previous = null;
		while (true)
		{
			ct.ThrowIfCancellationRequested();
			var status = worker.Status;
			if (status != previous) { previous = status; yield return status; }
			await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
		}
	}
}
