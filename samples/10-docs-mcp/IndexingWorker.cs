using System.Threading.Channels;
using DocsMcpSample.Search;

namespace DocsMcpSample;

internal sealed record IndexStatus(bool Ready, IndexGeneration? Generation, string Phase, bool Rebuilding, int DocumentsProcessed, int SectionsProcessed, DateTimeOffset? StartedAt, string? LastError)
{
	public DateTimeOffset? NextRetryAt { get; init; }
}

internal sealed class IndexingWorker(DocumentIndex index, MarkdownCorpus corpus, ILogger<IndexingWorker> logger, TimeProvider time) : BackgroundService
{
	private sealed record WorkerState(IndexStatus Status, Func<Task>? Cancel = null);
	private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(1);
	private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);
	private int _failures;
	private string? _failedFingerprint;
	private DateTimeOffset _retryAt;
	private WorkerState _state = new(new(false, null, "Starting", false, 0, 0, null, null));
	internal IndexStatus Status => Volatile.Read(ref _state).Status;
	internal void RequireReady()
	{
		var status = Status;
		if (status.Ready) { return; }
		throw new IndexUnavailableException(status.LastError is null
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
			using var timeout = new CancellationTokenSource(ScanInterval, time);
			using var scan = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
			try { force = await _requests.Reader.ReadAsync(scan.Token); }
			catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { force = false; }
		}
	}

	private async Task RefreshAsync(bool force, CancellationToken stoppingToken)
	{
		string? fingerprint = null;
		if (!force && _failures > 0 && _failedFingerprint is null && time.GetUtcNow() < _retryAt) { return; }
		try
		{
			index.Initialize();
			var published = index.Generation();
			SetStatus(Status with { Ready = published is not null, Generation = published, Phase = "Checking documents" });
			fingerprint = await corpus.FingerprintAsync(stoppingToken);
			if (!force && Status.Generation?.Fingerprint == fingerprint)
			{
				ClearRetry();
				SetStatus(Status with { Phase = "Up to date" });
				return;
			}
			if (!force && _failures > 0 && fingerprint == _failedFingerprint && time.GetUtcNow() < _retryAt)
			{
				SetStatus(Status with { Phase = "Retry scheduled" });
				return;
			}
			await RebuildAsync(stoppingToken);
			ClearRetry();
		}
		catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
		{
			Hosting.Log.IndexCancelled(logger);
			SetStatus(Status with { Phase = "Rebuild cancelled" });
			ClearRetry();
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			_failures = Math.Min(_failures + 1, 5);
			_failedFingerprint = fingerprint;
			_retryAt = time.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(60 * (1 << (_failures - 1)), 900));
			Hosting.Log.IndexFailure(logger, error, _retryAt);
			SetStatus(Status with { Phase = "Indexing failed", LastError = error.Message, NextRetryAt = _retryAt });
		}
	}

	private void ClearRetry()
	{
		_failures = 0;
		_failedFingerprint = null;
		SetStatus(Status with { NextRetryAt = null });
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
			NextRetryAt = null,
		};
		Volatile.Write(ref _state, new(rebuilding, CancelAsync));
		Hosting.Log.IndexStarted(logger);
		try
		{
			var generation = await index.RebuildAsync(corpus, (documents, sections) =>
			{
				SetStatus(Status with { DocumentsProcessed = documents, SectionsProcessed = sections });
			}, build.Token);
			SetStatus(Status with { Ready = true, Generation = generation, Phase = "Up to date", LastError = null });
			Hosting.Log.IndexPublished(logger, generation.Id, generation.Documents, generation.Sections);
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
