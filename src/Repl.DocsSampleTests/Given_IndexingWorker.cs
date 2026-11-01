using DocsMcpSample;
using Microsoft.Extensions.Logging.Abstractions;

namespace Repl.DocsSampleTests;

[TestClass]
[AssetsAvailable]
public sealed class Given_IndexingWorker
{
	[TestMethod]
	[Description("Live worker controls cancel a paused replacement without losing readiness, then accept another rebuild and publish it successfully.")]
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
			await worker.CancelRebuildAsync().ConfigureAwait(false);
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
