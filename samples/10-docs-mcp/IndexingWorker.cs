using System.Threading.Channels;
using DocsMcpSample.Search;

namespace DocsMcpSample;

internal sealed record IndexStatus(bool Ready, IndexGeneration? Generation, string Phase, bool Rebuilding, int DocumentsProcessed, int SectionsProcessed, DateTimeOffset? StartedAt, string? LastError);

internal sealed class IndexingWorker(DocumentIndex index, MarkdownCorpus corpus, ILogger<IndexingWorker> logger, TimeProvider time) : BackgroundService
{
	private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(1);
	private readonly Lock _gate = new();
	private IndexStatus _status = new(false, null, "Starting", false, 0, 0, null, null);
	private CancellationTokenSource? _build;
	internal IndexStatus Status { get { lock (_gate) { return _status; } } }
	internal void RequireReady()
	{
		var status = Status;
		if (status.Ready) { return; }
		throw new InvalidOperationException(status.LastError is null
			? "No documentation index is ready yet. Indexing may be running or cancelled; ask the administrator to check index status."
			: "The documentation index could not be built. Ask the administrator to check index status for the failure.");
	}

	internal string RequestRebuild()
	{
		lock (_gate)
		{
			return _status.Rebuilding ? "A rebuild is already running." : _requests.Writer.TryWrite(true) ? "Rebuild requested." : "A rebuild is already queued.";
		}
	}

	internal async Task<string> CancelRebuildAsync()
	{
		Task? cancellation;
		lock (_gate) { cancellation = _build?.CancelAsync(); }
		if (cancellation is null) { return "No rebuild is running."; }
		await cancellation;
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
			lock (_gate) { _status = _status with { Ready = published is not null, Generation = published, Phase = "Checking documents" }; }
			var fingerprint = await corpus.FingerprintAsync(stoppingToken);
			if (!force && Status.Generation?.Fingerprint == fingerprint)
			{
				lock (_gate) { _status = _status with { Phase = "Up to date" }; }
				return;
			}
			using var build = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
			lock (_gate)
			{
				_build = build;
				_status = _status with { Rebuilding = true, Phase = "Indexing documents", DocumentsProcessed = 0, SectionsProcessed = 0, StartedAt = time.GetUtcNow() };
			}
			try
			{
				var generation = await index.RebuildAsync(corpus, (documents, sections) =>
				{
					lock (_gate) { _status = _status with { DocumentsProcessed = documents, SectionsProcessed = sections }; }
				}, build.Token);
				lock (_gate) { _status = _status with { Ready = true, Generation = generation, Phase = "Up to date", LastError = null }; }
			}
			finally
			{
				lock (_gate) { _build = null; _status = _status with { Rebuilding = false }; }
			}
		}
		catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
		{
			lock (_gate) { _status = _status with { Phase = "Rebuild cancelled" }; }
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			Hosting.Log.IndexFailure(logger, error);
			lock (_gate) { _status = _status with { Phase = "Indexing failed", LastError = error.Message }; }
		}
	}
}
