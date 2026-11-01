using DocsMcpSample.Search;

namespace Repl.DocsSampleTests;

[TestClass]
public sealed class Given_MarkdownCorpus
{
	[TestMethod]
	[Description("Each chunk fits the actual embedding window while retaining source references and duplicate-heading identity.")]
	public void When_HeadingsRepeatAndParagraphsAreLong_Then_ChunksRemainBoundedAndAddressable()
	{
		using var fixture = new TestCorpus();
		var text = "# Guide\n\n## Console\n\n" + new string('a', 1300) + "😀\n\n## Console\n\nConnect to the console.\n";

		var document = fixture.Corpus.Parse("guide.md", text);

		document.Sections.Should().OnlyContain(section => fixture.Embedding.CountPassageTokens(section.Text) <= E5Embedding.MaxTokens);
		document.Sections.Select(section => section.Id).Should().OnlyHaveUniqueItems();
		document.Sections.Should().Contain(section => section.Anchor == "console-1");
		document.Sections.Should().OnlyContain(section => section.StartLine > 0 && section.EndLine >= section.StartLine && section.Source == "guide.md");
		string.Concat(document.Sections.Select(section => section.Text)).Should().Contain(new string('a', 1300) + "😀");
	}

	[TestMethod]
	[Description("Content hashes detect edits, removals and renames without relying on file timestamps.")]
	public async Task When_CorpusChanges_Then_FingerprintChanges()
	{
		using var fixture = new TestCorpus();
		await fixture.WriteAsync("one.md", "# One").ConfigureAwait(false);
		var original = await fixture.Corpus.FingerprintAsync(CancellationToken.None).ConfigureAwait(false);

		await fixture.WriteAsync("one.md", "# Two").ConfigureAwait(false);
		var edited = await fixture.Corpus.FingerprintAsync(CancellationToken.None).ConfigureAwait(false);
		File.Move(Path.Combine(fixture.Settings.Documents, "one.md"), Path.Combine(fixture.Settings.Documents, "two.md"));
		var renamed = await fixture.Corpus.FingerprintAsync(CancellationToken.None).ConfigureAwait(false);

		edited.Should().NotBe(original);
		renamed.Should().NotBe(edited);
	}

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

	[TestMethod]
	[Description("Heading syntax and duplicate slugs cannot create conflicting identifiers, including a preamble before a Document heading.")]
	public void When_HeadingSlugsCollide_Then_SectionIdentifiersRemainUnique()
	{
		using var fixture = new TestCorpus();
		var document = fixture.Corpus.Parse("guide.md", "Preamble.\n\n# Document\n\nOne.\n\n# Foo\n\nTwo.\n\n# Foo-1\n\nThree.\n\n# Foo\n\nFour.\n\nSetext *heading*\n===============\n\nFive.");

		document.Sections.Select(section => section.Id).Should().OnlyHaveUniqueItems();
		document.Sections.Should().Contain(section => section.Heading == "Setext heading");
	}
}
