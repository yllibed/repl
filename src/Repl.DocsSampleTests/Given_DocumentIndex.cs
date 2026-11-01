using DocsMcpSample.Search;

namespace Repl.DocsSampleTests;

[TestClass]
[AssetsAvailable]
public sealed class Given_DocumentIndex
{
	[TestMethod]
	[Description("The real SQLite extension publishes and queries keywords and vectors together, and fetch only resolves indexed identifiers.")]
	public async Task When_AReplacementIsPublished_Then_SearchAndFetchUseTheSameGeneration()
	{
		using var fixture = new TestCorpus();
		fixture.Index.Initialize();
		await fixture.WriteAsync("console.md", "# Console\n\nConnect to the running console.").ConfigureAwait(false);
		await fixture.WriteAsync("health.md", "# Health\n\nCheck process liveness.").ConfigureAwait(false);

		var generation = await fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None).ConfigureAwait(false);
		var results = fixture.Index.Search("console", 5, CancellationToken.None);
		var document = fixture.Index.Fetch(results.Hybrid[0].Id, 20000);

		results.Generation.Should().Be(generation.Id);
		results.Keyword.Should().NotBeEmpty();
		results.Vector.Should().HaveCount(2);
		document.Generation.Should().Be(generation.Id);
		document.Text.Should().Contain("running console");
		var traversal = () => fixture.Index.Fetch("../../secret", 20000);
		traversal.Should().Throw<KeyNotFoundException>();
	}

	[TestMethod]
	[Description("Cancellation after replacement preparation starts preserves both the searchable rows and generation metadata.")]
	public async Task When_ARebuildIsCancelled_Then_ThePublishedIndexSurvives()
	{
		using var fixture = new TestCorpus();
		fixture.Index.Initialize();
		await fixture.WriteAsync("one.md", "# Console\n\nOld console.").ConfigureAwait(false);
		var original = await fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None).ConfigureAwait(false);
		await fixture.WriteAsync("one.md", "# Console\n\nNew console.").ConfigureAwait(false);
		using var cancellation = new CancellationTokenSource();
		fixture.Embedding.BeforeEmbedding = cancellation.Cancel;

		var rebuild = () => fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, cancellation.Token);
		await rebuild.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		fixture.Embedding.BeforeEmbedding = null;

		fixture.Index.Generation()!.Id.Should().Be(original.Id);
		var hit = fixture.Index.Search("console", 5, CancellationToken.None).Hybrid[0];
		fixture.Index.Fetch(hit.Id, 20000).Text.Should().Contain("Old console");
	}

	[TestMethod]
	[Description("Deleted sources disappear on publication, a new index instance reopens the same generation, and empty corpora are valid.")]
	public async Task When_DocumentsAreRemoved_Then_WarmRestartSeesTheReplacement()
	{
		using var fixture = new TestCorpus();
		fixture.Index.Initialize();
		await fixture.WriteAsync("one.md", "# Console").ConfigureAwait(false);
		await fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None).ConfigureAwait(false);
		File.Delete(Path.Combine(fixture.Settings.Documents, "one.md"));

		var empty = await fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None).ConfigureAwait(false);
		var reopened = new DocumentIndex(fixture.Settings, fixture.Embedding, TimeProvider.System);

		reopened.Generation()!.Id.Should().Be(empty.Id);
		reopened.Search("console", 5, CancellationToken.None).Hybrid.Should().BeEmpty();
	}

	[TestMethod]
	[Description("Readers keep the previous generation during preparation, and a build failure never exposes staged content.")]
	public async Task When_EmbeddingFailsDuringRebuild_Then_ConcurrentReadersKeepTheOldGeneration()
	{
		using var fixture = new TestCorpus();
		fixture.Index.Initialize();
		await fixture.WriteAsync("one.md", "# Console\n\nOriginal.").ConfigureAwait(false);
		var original = await fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None).ConfigureAwait(false);
		await fixture.WriteAsync("one.md", "# Replacement\n\nChanged.").ConfigureAwait(false);
		fixture.Embedding.BeforeEmbedding = () =>
		{
			fixture.Index.Generation()!.Id.Should().Be(original.Id);
			throw new IOException("Injected inference failure");
		};

		var rebuild = () => fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None);
		await rebuild.Should().ThrowAsync<IOException>().ConfigureAwait(false);

		fixture.Index.Generation()!.Id.Should().Be(original.Id);
	}

	[TestMethod]
	[Description("Long headings and colliding heading slugs produce bounded identifiers that search clients can fetch.")]
	public async Task When_HeadingNamesAreUnusual_Then_AllSearchResultsCanBeFetched()
	{
		using var fixture = new TestCorpus();
		fixture.Index.Initialize();
		await fixture.WriteAsync("one.md", "Preamble.\n\n# Document\n\nBody.\n\n# Foo\n\nBody.\n\n# Foo-1\n\nBody.\n\n# Foo\n\nBody.\n\n# " + new string('a', 320) + "\n\nBody.").ConfigureAwait(false);
		await fixture.Index.RebuildAsync(fixture.Corpus, (_, _) => { }, CancellationToken.None).ConfigureAwait(false);

		var hits = fixture.Index.Search("Body", 20, CancellationToken.None).Hybrid;

		hits.Should().NotBeEmpty();
		foreach (var hit in hits)
		{
			hit.Id.Length.Should().BeLessThan(300);
			fixture.Index.Fetch(hit.Id, 20000).Id.Should().Be(hit.Id);
		}
	}
}
