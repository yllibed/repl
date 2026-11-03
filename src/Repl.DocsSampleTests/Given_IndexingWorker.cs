using DocsMcpSample;
using Microsoft.Extensions.Logging.Abstractions;

namespace Repl.DocsSampleTests;

[TestClass]
[AssetsAvailable]
public sealed class Given_IndexingWorker
{
	[TestMethod]
	[Description("Repeated failures delay automatic rebuilds; manual requests and changed documents bypass the delay, and a successful build resets it.")]
	public async Task When_RebuildsKeepFailing_Then_RetriesBackOffWithoutBlockingRecovery()
	{
		using var fixture = new TestCorpus();
		await fixture.WriteAsync("guide.md", "# Guide\n\nOriginal.").ConfigureAwait(false);
		var attempts = 0;
		var fail = 1;
		fixture.Embedding.BeforeEmbedding = () =>
		{
			Interlocked.Increment(ref attempts);
			if (Volatile.Read(ref fail) == 1) { throw new IOException("Temporary storage failure."); }
		};
		var clock = new ObservedTimeProvider(fixture.Clock);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		using var worker = new IndexingWorker(fixture.Index, fixture.Corpus, NullLogger<IndexingWorker>.Instance, clock);
		await worker.StartAsync(deadline.Token).ConfigureAwait(false);
		try
		{
			await WaitForAsync(() => clock.Timers >= 1, deadline.Token).ConfigureAwait(false);
			Volatile.Read(ref attempts).Should().Be(1);

			fixture.Clock.Advance(TimeSpan.FromMinutes(1));
			await WaitForAsync(() => clock.Timers >= 2, deadline.Token).ConfigureAwait(false);
			Volatile.Read(ref attempts).Should().Be(2);

			fixture.Clock.Advance(TimeSpan.FromMinutes(1));
			await WaitForAsync(() => clock.Timers >= 3, deadline.Token).ConfigureAwait(false);
			Volatile.Read(ref attempts).Should().Be(2, because: "the second failure waits two minutes before rebuilding the same corpus");

			worker.RequestRebuild();
			await WaitForAsync(() => clock.Timers >= 4, deadline.Token).ConfigureAwait(false);
			Volatile.Read(ref attempts).Should().Be(3);
			Interlocked.Exchange(ref fail, 0);
			await fixture.WriteAsync("guide.md", "# Guide\n\nChanged during the retry delay.").ConfigureAwait(false);
			fixture.Clock.Advance(TimeSpan.FromMinutes(1));
			await WaitForAsync(() => clock.Timers >= 5, deadline.Token).ConfigureAwait(false);
			worker.Status.Ready.Should().BeTrue();
			worker.Status.LastError.Should().BeNull();
			Volatile.Read(ref attempts).Should().Be(4);

			Interlocked.Exchange(ref fail, 1);
			await fixture.WriteAsync("guide.md", "# Guide\n\nAnother replacement.").ConfigureAwait(false);
			worker.RequestRebuild();
			await WaitForAsync(() => clock.Timers >= 6, deadline.Token).ConfigureAwait(false);
			fixture.Clock.Advance(TimeSpan.FromMinutes(1));
			await WaitForAsync(() => clock.Timers >= 7, deadline.Token).ConfigureAwait(false);
			Volatile.Read(ref attempts).Should().Be(6, because: "success resets the next failure's retry delay to one minute");
			worker.Status.Ready.Should().BeTrue();
		}
		finally { await worker.StopAsync(deadline.Token).ConfigureAwait(false); }
	}

	[TestMethod]
	[Description("A persistent failure continues to receive automatic attempts within the maximum retry delay instead of being blacklisted indefinitely.")]
	public async Task When_AFailurePersists_Then_TheAutomaticRetryDelayRemainsCapped()
	{
		using var fixture = new TestCorpus();
		await fixture.WriteAsync("guide.md", "# Guide\n\nCannot currently be embedded.").ConfigureAwait(false);
		var attempts = 0;
		fixture.Embedding.BeforeEmbedding = () => { Interlocked.Increment(ref attempts); throw new IOException("Temporary failure."); };
		var clock = new ObservedTimeProvider(fixture.Clock);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		using var worker = new IndexingWorker(fixture.Index, fixture.Corpus, NullLogger<IndexingWorker>.Instance, clock);
		await worker.StartAsync(deadline.Token).ConfigureAwait(false);
		try
		{
			await WaitForAsync(() => clock.Timers >= 1, deadline.Token).ConfigureAwait(false);
			for (var retry = 0; retry < 8; retry++)
			{
				var timers = clock.Timers;
				fixture.Clock.Advance(TimeSpan.FromMinutes(15));
				await WaitForAsync(() => clock.Timers > timers, deadline.Token).ConfigureAwait(false);
				Volatile.Read(ref attempts).Should().Be(2 + retry);
			}
		}
		finally { await worker.StopAsync(deadline.Token).ConfigureAwait(false); }
	}

	[TestMethod]
	[Description("Restoring the published corpus after a failed replacement clears an obsolete retry and resets the delay for a later unrelated failure.")]
	public async Task When_TheCorpusIsRestored_Then_ObsoleteRetryStateIsCleared()
	{
		using var fixture = new TestCorpus();
		const string original = "# Guide\n\nPublished content.";
		await fixture.WriteAsync("guide.md", original).ConfigureAwait(false);
		fixture.Index.Initialize();
		await fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None).ConfigureAwait(false);
		fixture.Embedding.BeforeEmbedding = () => throw new IOException("Temporary failure.");
		var clock = new ObservedTimeProvider(fixture.Clock);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		using var worker = new IndexingWorker(fixture.Index, fixture.Corpus, NullLogger<IndexingWorker>.Instance, clock);
		await worker.StartAsync(deadline.Token).ConfigureAwait(false);
		try
		{
			await WaitForAsync(() => clock.Timers >= 1, deadline.Token).ConfigureAwait(false);
			await fixture.WriteAsync("guide.md", "# Guide\n\nFailed replacement.").ConfigureAwait(false);
			worker.RequestRebuild();
			await WaitForAsync(() => clock.Timers >= 2, deadline.Token).ConfigureAwait(false);
			worker.Status.NextRetryAt.Should().NotBeNull();

			await fixture.WriteAsync("guide.md", original).ConfigureAwait(false);
			fixture.Clock.Advance(TimeSpan.FromMinutes(1));
			await WaitForAsync(() => clock.Timers >= 3, deadline.Token).ConfigureAwait(false);
			worker.Status.Phase.Should().Be("Up to date");
			worker.Status.NextRetryAt.Should().BeNull();

			await fixture.WriteAsync("guide.md", "# Guide\n\nAnother failure.").ConfigureAwait(false);
			worker.RequestRebuild();
			await WaitForAsync(() => clock.Timers >= 4, deadline.Token).ConfigureAwait(false);
			worker.Status.NextRetryAt.Should().Be(fixture.Clock.GetUtcNow() + TimeSpan.FromMinutes(1));
		}
		finally { await worker.StopAsync(deadline.Token).ConfigureAwait(false); }
	}

	private sealed class ObservedTimeProvider(TimeProvider inner) : TimeProvider
	{
		private int _timers;
		internal int Timers => Volatile.Read(ref _timers);
		public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			var timer = inner.CreateTimer(callback, state, dueTime, period);
			Interlocked.Increment(ref _timers);
			return timer;
		}
	}

	[TestMethod]
	[Description("Concurrent readers and administration requests cancel a paused replacement without losing the published snapshot, then allow another build to succeed.")]
	public async Task When_ARequestedBuildIsCancelled_Then_AnotherRequestCanPublish()
	{
		using var fixture = new TestCorpus();
		fixture.Index.Initialize();
		await fixture.WriteAsync("console.md", "# Console\n\nOriginal console.").ConfigureAwait(false);
		var original = await fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None).ConfigureAwait(false);
		original.PublishedAt.Should().Be(fixture.Clock.GetUtcNow());
		var id = fixture.Index.List(0, 1)[0].Id;
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		using var worker = new IndexingWorker(fixture.Index, fixture.Corpus, NullLogger<IndexingWorker>.Instance, fixture.Clock);
		await worker.StartAsync(deadline.Token).ConfigureAwait(false);
		try
		{
			await WaitForAsync(() => worker.Status.Ready && string.Equals(worker.Status.Phase, "Up to date", StringComparison.Ordinal), deadline.Token).ConfigureAwait(false);
			await fixture.WriteAsync("console.md", "# Console\n\nReplacement console.").ConfigureAwait(false);
			var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using var pause = new ManualResetEventSlim();
			fixture.Embedding.Preparing = ct => { entered.TrySetResult(); pause.Wait(ct); };

			worker.RequestRebuild().Should().Be("Rebuild requested.");
			await entered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
			fixture.Index.Fetch(id, 20000).Text.Should().Contain("Original console");
			worker.Status.StartedAt.Should().Be(fixture.Clock.GetUtcNow());
			await AssertConcurrentControlsAsync(worker, original.Id, deadline.Token).ConfigureAwait(false);
			await WaitForAsync(() => !worker.Status.Rebuilding, deadline.Token).ConfigureAwait(false);

			worker.Status.Ready.Should().BeTrue();
			fixture.Index.Generation()!.Id.Should().Be(original.Id);
			fixture.Embedding.Preparing = null;
			worker.RequestRebuild().Should().Be("Rebuild requested.");
			await WaitForAsync(() => !string.Equals(worker.Status.Generation?.Id, original.Id, StringComparison.Ordinal), deadline.Token).ConfigureAwait(false);
			fixture.Index.Fetch(id, 20000).Text.Should().Contain("Replacement console");
		}
		finally { await worker.StopAsync(deadline.Token).ConfigureAwait(false); }
	}

	private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
	{
		while (!condition()) { await Task.Delay(10, cancellationToken).ConfigureAwait(false); }
	}

	[TestMethod]
	[Description("Worker shutdown waits for a cancellation callback that outlives indexing, keeping its token source usable until the callback completes.")]
	public async Task When_IndexingUnwindsDuringCancellation_Then_ShutdownWaitsForTheCallbacks()
	{
		using var fixture = new TestCorpus();
		await fixture.WriteAsync("console.md", "# Console\n\nPaused console.").ConfigureAwait(false);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		using var releaseCallback = new ManualResetEventSlim();
		using var pauseEmbedding = new ManualResetEventSlim();
		var embeddingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		CancellationToken embeddingToken = default;
		fixture.Embedding.Preparing = ct =>
		{
			embeddingToken = ct;
			_ = ct.Register(() => { callbackEntered.TrySetResult(); releaseCallback.Wait(deadline.Token); });
			embeddingEntered.TrySetResult();
			pauseEmbedding.Wait(ct);
		};
		using var worker = new IndexingWorker(fixture.Index, fixture.Corpus, NullLogger<IndexingWorker>.Instance, fixture.Clock);
		await worker.StartAsync(deadline.Token).ConfigureAwait(false);
		try
		{
			await embeddingEntered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
			var cancellation = worker.CancelRebuildAsync();
			await callbackEntered.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
			await WaitForAsync(() => !worker.Status.Rebuilding, deadline.Token).ConfigureAwait(false);

			var stopping = worker.StopAsync(deadline.Token);
			stopping.IsCompleted.Should().BeFalse();
			cancellation.IsCompleted.Should().BeFalse();
			embeddingToken.WaitHandle.Should().NotBeNull();
			releaseCallback.Set();
			await Task.WhenAll(cancellation, stopping).WaitAsync(deadline.Token).ConfigureAwait(false);

			worker.ExecuteTask!.IsCompleted.Should().BeTrue();
		}
		finally
		{
			releaseCallback.Set();
			await worker.StopAsync(deadline.Token).ConfigureAwait(false);
		}
	}

	private static async Task AssertConcurrentControlsAsync(IndexingWorker worker, string generation, CancellationToken cancellationToken)
	{
		var rebuilds = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(worker.RequestRebuild, cancellationToken))).ConfigureAwait(false);
		rebuilds.Should().OnlyContain(reply => reply == "A rebuild is already running.");

		var cancellations = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
		{
			var snapshot = worker.Status;
			snapshot.Ready.Should().BeTrue();
			snapshot.Generation!.Id.Should().Be(generation);
			return await worker.CancelRebuildAsync().ConfigureAwait(false);
		}, cancellationToken))).ConfigureAwait(false);

		cancellations.Should().Contain("Cancellation requested; the last published index remains available.");
	}

	[TestMethod]
	[Description("A damaged initial database leaves administration alive with a clear failure, and a manual request can retry after the storage is repaired.")]
	public async Task When_InitializationFails_Then_TheWorkerReportsFailureAndCanRetry()
	{
		using var fixture = new TestCorpus();
		var database = Path.Combine(fixture.Settings.Index, "documents.db");
		await File.WriteAllTextAsync(database, "not a SQLite database").ConfigureAwait(false);
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		using var worker = new IndexingWorker(fixture.Index, fixture.Corpus, NullLogger<IndexingWorker>.Instance, fixture.Clock);
		await worker.StartAsync(deadline.Token).ConfigureAwait(false);
		try
		{
			await WaitForAsync(() => worker.Status.LastError is not null, deadline.Token).ConfigureAwait(false);
			worker.Status.Ready.Should().BeFalse();
			var query = worker.RequireReady;
			query.Should().Throw<InvalidOperationException>().WithMessage("*could not be built*");

			File.Delete(database);
			worker.RequestRebuild();
			await WaitForAsync(() => worker.Status.Ready, deadline.Token).ConfigureAwait(false);

			worker.Status.LastError.Should().BeNull();
		}
		finally { await worker.StopAsync(deadline.Token).ConfigureAwait(false); }
	}
}
