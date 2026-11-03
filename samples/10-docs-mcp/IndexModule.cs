using System.Runtime.CompilerServices;
using DocsMcpSample.Search;
using Repl;
using Repl.Parameters;
using Repl.Spectre;
using Spectre.Console;

namespace DocsMcpSample;

internal sealed class IndexModule : IReplModule
{
	public void Map(IReplMap map)
	{
		map.Context("index", index =>
		{
			index.Map("status", ([FromServices] IndexingWorker worker) => worker.Status).WithDescription("Show the published generation and current indexing activity.").ReadOnly();
			index.Map("rebuild", ([FromServices] IndexingWorker worker) => worker.RequestRebuild()).WithDescription("Queue a full rebuild; the current index remains available.");
			index.Map("cancel", async ([FromServices] IndexingWorker worker) => await worker.CancelRebuildAsync()).WithDescription("Cancel rebuilding without deleting the published index.");
			index.Map("watch", WatchAsync).WithDescription("Watch indexing live. Ctrl+C stops observing, not indexing.");
		}).WithDetails("Inspect and control indexing in the running process.");
	}

	private static async Task WatchAsync([FromServices] IndexingWorker worker, [FromServices] IAnsiConsole console, [FromServices] SpectreInteractionPresenter presenter, IReplIoContext io, IReplPagingContext paging, CancellationToken ct)
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
