using System.Text.Json;
using DocsMcpSample;
using DocsMcpSample.Search;

namespace Repl.DocsSampleTests;

[TestClass]
[AssetsAvailable]
public sealed class Given_E5Embedding
{
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
