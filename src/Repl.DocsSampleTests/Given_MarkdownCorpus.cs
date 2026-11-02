using DocsMcpSample.Search;

namespace Repl.DocsSampleTests;

[TestClass]
public sealed class Given_MarkdownCorpus
{
	[TestMethod]
	[DataRow(32)]
	[DataRow(1024)]
	[Description("Passage preparation follows the selected model's token window, including windows smaller and larger than the bundled model's.")]
	public void When_TheModelWindowChanges_Then_ChunksUseThatWindow(int window)
	{
		using var fixture = new TestCorpus();
		var embedding = new TestEmbedding { MaxTokens = window };
		var corpus = new MarkdownCorpus(fixture.Settings, embedding);
		var text = new string('a', 1500) + "😀";

		var document = corpus.Parse("guide.md", text);

		document.Sections.Should().OnlyContain(section => embedding.CountPassageTokens(section.Text) <= window);
		document.Sections.Max(section => embedding.CountPassageTokens(section.Text)).Should().Be(window);
		string.Concat(document.Sections.Select(section => section.Text)).Should().Be(text);
	}
	[TestMethod]
	[Description("Each chunk fits the actual embedding window while retaining source references and duplicate-heading identity.")]
	public void When_HeadingsRepeatAndParagraphsAreLong_Then_ChunksRemainBoundedAndAddressable()
	{
		using var fixture = new TestCorpus();
		var text = "# Guide\n\n## Console\n\n" + new string('a', 1300) + "😀\n\n## Console\n\nConnect to the console.\n";

		var document = fixture.Corpus.Parse("guide.md", text);

		document.Sections.Should().OnlyContain(section => fixture.Embedding.CountPassageTokens(section.Text) <= fixture.Embedding.MaxTokens);
		document.Sections.Select(section => section.Id).Should().OnlyHaveUniqueItems();
		document.Sections.Should().Contain(section => section.Anchor == "console-1");
		document.Sections.Should().OnlyContain(section => section.StartLine > 0 && section.EndLine >= section.StartLine && section.Source == "guide.md");
		string.Concat(document.Sections.Select(section => section.Text)).Should().Contain(new string('a', 1300) + "😀");
	}

	[TestMethod]
	[Description("A long single block is chunked with bounded tokenizer probes instead of repeatedly tokenizing the entire remaining document.")]
	public void When_ABlockIsLarge_Then_TokenizerWorkRemainsBounded()
	{
		using var fixture = new TestCorpus();
		long characters = 0;
		var largestProbe = 0;
		fixture.Embedding.Counting = length => { characters += length; largestProbe = Math.Max(largestProbe, length); };
		var text = new string('a', 65536);

		var document = fixture.Corpus.Parse("large.md", text);

		string.Concat(document.Sections.Select(section => section.Text)).Should().Be(text);
		largestProbe.Should().BeLessThanOrEqualTo(8192);
		characters.Should().BeLessThan(40L * text.Length);
	}

	[TestMethod]
	[Description("Cancellation raised while probing a long block stops passage preparation before another tokenizer call.")]
	public void When_ParsingIsCancelled_Then_TokenizerProbingStops()
	{
		using var fixture = new TestCorpus();
		using var cancellation = new CancellationTokenSource();
		var probes = 0;
#pragma warning disable MA0045 // Token counting is synchronous; cancellation must be visible before the next probe.
		fixture.Embedding.Counting = _ => { probes++; cancellation.Cancel(); };
#pragma warning restore MA0045

		var parse = () => fixture.Corpus.Parse("large.md", new string('a', 65536), cancellation.Token);

		parse.Should().Throw<OperationCanceledException>();
		probes.Should().Be(1);
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
	[Description("Heading syntax and duplicate slugs cannot create conflicting identifiers, including a preamble before a Document heading.")]
	public void When_HeadingSlugsCollide_Then_SectionIdentifiersRemainUnique()
	{
		using var fixture = new TestCorpus();
		var document = fixture.Corpus.Parse("guide.md", "Preamble.\n\n# Document\n\nOne.\n\n# Foo\n\nTwo.\n\n# Foo-1\n\nThree.\n\n# Foo\n\nFour.\n\nSetext *heading*\n===============\n\nFive.");

		document.Sections.Select(section => section.Id).Should().OnlyHaveUniqueItems();
		document.Sections.Should().Contain(section => section.Heading == "Setext heading");
	}
}
