using System.Text.Json;
using DocsMcpSample;
using DocsMcpSample.Search;

namespace Repl.DocsSampleTests;

[TestClass]
[AssetsAvailable]
public sealed class Given_E5Embedding
{
	[TestMethod]
	[DataRow("\n")]
	[DataRow("\r\n")]
	[Description("Contextual chunk budgets count exactly the passage-prefixed XLM-R input, including both special tokens.")]
	public void When_ContextualPassagesArePrepared_Then_TheRealEncoderAcceptsEveryChunk(string newline)
	{
		using var fixture = new TestCorpus();
		using var embedding = new E5Embedding(fixture.Settings);
		var corpus = new MarkdownCorpus(fixture.Settings, embedding);
		var title = string.Concat(Enumerable.Repeat("文😀", 1000));
		var heading = string.Concat(Enumerable.Repeat("章😀", 1000));
		var body = string.Join(newline, Enumerable.Repeat("日本語 café 😀 console commands and connection settings.", 300));

		var document = corpus.Parse("guide.md", "# " + title + newline + "## " + heading + newline + body);

		document.Sections.Should().HaveCountGreaterThan(1);
		string.Concat(document.Sections.Select(section => section.Text)).Should().Be(body);
		foreach (var section in document.Sections)
		{
			var input = MarkdownCorpus.EmbeddingText(section.Title, section.Heading, section.Text);
			var ids = embedding.Encode("passage: " + input);
			embedding.CountPassageTokens(input).Should().Be(ids.Length);
			ids.Length.Should().BeLessThanOrEqualTo(512);
			ids[0].Should().Be(0);
			ids[^1].Should().Be(2);
		}
	}

	[TestMethod]
	[Description("Pinned assets reproduce reference XLM-R token IDs and normalized PyTorch embeddings, including non-Latin input.")]
	public async Task When_TheRealModelRuns_Then_TokenizationAndVectorsMatchTheReference()
	{
		using var fixture = new TestCorpus();
		await Assets.VerifyAsync(fixture.Settings.Assets, CancellationToken.None).ConfigureAwait(false);
		using var embedding = new E5Embedding(fixture.Settings);
		var reference = JsonSerializer.Deserialize<ReferenceItem[]>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "embedding-reference.json")).ConfigureAwait(false), JsonSerializerOptions.Web)!;
		reference.Should().NotBeEmpty();

		foreach (var item in reference)
		{
			embedding.Encode(item.Text).Should().Equal(item.Ids);
			var query = item.Text.StartsWith("query: ", StringComparison.Ordinal);
			var vector = embedding.Embed(item.Text[(query ? 7 : 9)..], query, CancellationToken.None);
			var cosine = vector.Zip(item.Vector, (left, right) => (double)left * right).Sum() /
				Math.Sqrt(vector.Sum(value => (double)value * value) * item.Vector.Sum(value => (double)value * value));

			cosine.Should().BeGreaterThan(.999);
			vector.Sum(value => (double)value * value).Should().BeApproximately(1, .0001);
		}
	}

	private sealed record ReferenceItem(string Text, long[] Ids, float[] Vector);
}
