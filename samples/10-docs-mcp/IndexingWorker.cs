using System.Threading.Channels;
using DocsMcpSample.Search;

namespace DocsMcpSample;

internal sealed record IndexStatus(bool Ready, IndexGeneration? Generation, string Phase, bool Rebuilding, int DocumentsProcessed, int SectionsProcessed, DateTimeOffset? StartedAt, string? LastError);

internal sealed class IndexingWorker(DocumentIndex index, MarkdownCorpus corpus, ILogger<IndexingWorker> logger, TimeProvider time) : BackgroundService
{
	private sealed record WorkerState(IndexStatus Status, Func<Task>? Cancel = null);
	private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(1);
	private WorkerState _state = new(new(false, null, "Starting", false, 0, 0, null, null));
	internal IndexStatus Status => Volatile.Read(ref _state).Status;
	internal void RequireReady()
	{
		var status = Status;
		if (status.Ready) { return; }
		throw new InvalidOperationException(status.LastError is null
			? "No documentation index is ready yet. Indexing may be running or cancelled; ask the administrator to check index status."
			: "The documentation index could not be built. Ask the administrator to check index status for the failure.");
	}

	internal string RequestRebuild() => Status.Rebuilding ? "A rebuild is already running."
		: _requests.Writer.TryWrite(true) ? "Rebuild requested." : "A rebuild is already queued.";

	internal async Task<string> CancelRebuildAsync()
	{
		var cancel = Volatile.Read(ref _state).Cancel;
		if (cancel is null) { return "No rebuild is running."; }
		await cancel();
		return "Cancellation requested; the last published index remains available.";
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var force = false;
		while (!stoppingToken.IsCancellationRequested)
		{
			await RefreshAsync(force, stoppingToken);
			using var scan = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
			scan.CancelAfter(TimeSpan.FromSeconds(60));
			try { force = await _requests.Reader.ReadAsync(scan.Token); }
			catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { force = false; }
		}
	}

	private async Task RefreshAsync(bool force, CancellationToken stoppingToken)
	{
		try
		{
			index.Initialize();
			var published = index.Generation();
			SetStatus(Status with { Ready = published is not null, Generation = published, Phase = "Checking documents" });
			var fingerprint = await corpus.FingerprintAsync(stoppingToken);
			if (!force && Status.Generation?.Fingerprint == fingerprint)
			{
				SetStatus(Status with { Phase = "Up to date" });
				return;
			}
			await RebuildAsync(stoppingToken);
		}
		catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
		{
			SetStatus(Status with { Phase = "Rebuild cancelled" });
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			Hosting.Log.IndexFailure(logger, error);
			SetStatus(Status with { Phase = "Indexing failed", LastError = error.Message });
		}
	}

	private async Task RebuildAsync(CancellationToken stoppingToken)
	{
		using var build = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
		var cancellationRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancellationForwarded = ForwardCancellationAsync(cancellationRequested.Task, build);
		Task CancelAsync()
		{
			cancellationRequested.TrySetResult(true);
			return cancellationForwarded;
		}
		var rebuilding = Status with
		{
			Rebuilding = true,
			Phase = "Indexing documents",
			DocumentsProcessed = 0,
			SectionsProcessed = 0,
			StartedAt = time.GetUtcNow(),
		};
		Volatile.Write(ref _state, new(rebuilding, CancelAsync));
		try
		{
			var generation = await index.RebuildAsync(corpus, (documents, sections) =>
			{
				SetStatus(Status with { DocumentsProcessed = documents, SectionsProcessed = sections });
			}, build.Token);
			SetStatus(Status with { Ready = true, Generation = generation, Phase = "Up to date", LastError = null });
		}
		finally
		{
			Volatile.Write(ref _state, new(Status with { Rebuilding = false }));
			cancellationRequested.TrySetResult(false);
			// A caller can retain an older snapshot; finish forwarding before disposing its token source.
			await cancellationForwarded;
		}
	}

	// Only the worker and its synchronous progress callback publish state; readers never modify it.
	private void SetStatus(IndexStatus status) => Volatile.Write(ref _state, _state with { Status = status });

	private static async Task ForwardCancellationAsync(Task<bool> requested, CancellationTokenSource target)
	{
		if (await requested) { await target.CancelAsync(); }
	}
}
