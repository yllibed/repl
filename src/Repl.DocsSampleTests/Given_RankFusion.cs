using DocsMcpSample.Search;

namespace Repl.DocsSampleTests;

[TestClass]
public sealed class Given_RankFusion
{
	[TestMethod]
	[Description("Fusion rewards a passage present in both lists and breaks ties deterministically by identifier.")]
	public void When_RankingsOverlap_Then_FusionCombinesRanksRatherThanRawScores()
	{
		var first = Hit("a", 1000);
		var shared = Hit("shared", -1000);

		var fused = DocumentIndex.Fuse([first, shared], [Hit("b", .99), shared], 3);

		fused[0].Id.Should().Be("shared");
		fused[1].Id.Should().Be("a");
		fused[0].Score.Should().BeApproximately(2d / 62, .000001);
	}

	private static SearchHit Hit(string id, double score) => new(id, "doc", "guide.md", "Guide", "Section", "section", 1, 2, "Excerpt", score);

}
