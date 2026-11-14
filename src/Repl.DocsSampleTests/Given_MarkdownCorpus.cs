using DocsMcpSample.Search;

namespace Repl.DocsSampleTests;

[TestClass]
public sealed class Given_MarkdownCorpus
{
	[TestMethod]
	[DataRow("\n")]
	[DataRow("\r\n")]
	[Description("Blank chunk boundaries and trailing empty lines do not create searchable whitespace-only evidence.")]
	public void When_AChunkBoundaryContainsOnlyWhitespace_Then_ItIsNotIndexed(string newline)
	{
		using var fixture = new TestCorpus();
		var corpus = new MarkdownCorpus(fixture.Settings, new TestEmbedding { MaxTokens = 32 });
		var text = new string('a', 24) + newline + newline + new string('b', 40) + newline + newline;

		var document = corpus.Parse("a.md", text);

		document.Sections.Should().NotBeEmpty();
		document.Sections.Should().OnlyContain(section => !string.IsNullOrWhiteSpace(section.Text));
		document.Sections.Should().OnlyContain(section => section.EndLine >= section.StartLine);
		document.Sections.Select(section => int.Parse(section.Id.AsSpan(section.Id.AsSpan().LastIndexOf(':') + 1), System.Globalization.CultureInfo.InvariantCulture))
			.Should().Equal(Enumerable.Range(0, document.Sections.Count));
	}

	[TestMethod]
	[DataRow("\n")]
	[DataRow("\r\n")]
	[Description("YAML metadata is excluded while the first real heading supplies the page title and source lines remain intact.")]
	public void When_FrontMatterPrecedesTheFirstHeading_Then_OnlyMarkdownSuppliesTheTitle(string newline)
	{
		using var fixture = new TestCorpus();
		var markdown = string.Join(newline, "---", "title: Wrong title", "description: metadata", "---", "", "Preamble.", "", "# Actual *title*", "", "Body.");

		var document = fixture.Corpus.Parse("guide.md", markdown);

		document.Title.Should().Be("Actual title");
		document.Sections.Should().OnlyContain(section => string.Equals(section.Title, "Actual title", StringComparison.Ordinal));
		document.Sections.Should().NotContain(section => section.Text.Contains("metadata", StringComparison.Ordinal));
		document.Sections.Single(section => section.Text.Contains("Body.", StringComparison.Ordinal)).StartLine.Should().Be(10);
		document.Text.Should().Be(markdown);
	}

	[TestMethod]
	[DataRow("\n")]
	[DataRow("\r\n")]
	[Description("Oversized paragraphs prefer complete LF or CRLF lines without normalizing source text.")]
	public void When_AnOversizedParagraphContainsNewlines_Then_ChunksPreserveSourceLines(string newline)
	{
		using var fixture = new TestCorpus();
		var embedding = new TestEmbedding { MaxTokens = 48 };
		var corpus = new MarkdownCorpus(fixture.Settings, embedding);
		var lines = Enumerable.Range(0, 9).Select(index => $"Line {index}: abcdefghij").ToArray();
		var text = string.Join(newline, lines);

		var document = corpus.Parse("a.md", text);

		string.Concat(document.Sections.Select(section => section.Text)).Should().Be(text);
		document.Sections.SkipLast(1).Should().OnlyContain(section => section.Text.EndsWith(newline, StringComparison.Ordinal));
		foreach (var section in document.Sections)
		{
			section.Text.Should().Be(string.Join(newline, lines[(section.StartLine - 1)..section.EndLine]) + (section.EndLine < lines.Length ? newline : ""));
		}
	}

	[TestMethod]
	[Description("The long-line fallback never splits a UTF-16 surrogate pair.")]
	public void When_ALongLineHasSurrogatePairs_Then_EachChunkRemainsValidUnicode()
	{
		using var fixture = new TestCorpus();
		var embedding = new TestEmbedding { MaxTokens = 32 };
		var corpus = new MarkdownCorpus(fixture.Settings, embedding);
		var text = string.Concat(Enumerable.Repeat("😀", 100));

		var document = corpus.Parse("a.md", text);

		string.Concat(document.Sections.Select(section => section.Text)).Should().Be(text);
		document.Sections.Should().HaveCountGreaterThan(1);
		foreach (var section in document.Sections)
		{
			foreach (var rune in section.Text.EnumerateRunes()) { rune.Value.Should().Be(0x1F600); }
			section.StartLine.Should().Be(1);
			section.EndLine.Should().Be(1);
			embedding.CountPassageTokens(MarkdownCorpus.EmbeddingText(section.Title, section.Heading, section.Text)).Should().BeLessThanOrEqualTo(32);
		}
	}

	[TestMethod]
	[Description("Original paragraph separators are retained within nonempty passages.")]
	public void When_AParagraphBreakHasSeveralBlankLines_Then_SourceTextIsNotNormalized()
	{
		using var fixture = new TestCorpus();
		const string text = "First.\r\n\r\n\r\nSecond.\r\n";

		var document = fixture.Corpus.Parse("a.md", text);

		string.Concat(document.Sections.Select(section => section.Text)).Should().Be(text);
	}

	[TestMethod]
	[Description("Embedding input includes the page title and current heading, not ancestor headings.")]
	public void When_ContextIsEmbedded_Then_ItContainsOnlyTitleAndCurrentHeading()
	{
		using var fixture = new TestCorpus();
		var document = fixture.Corpus.Parse("a.md", "# Guide\n## Ancestor\n### Current\n" + new string('a', 1000));

		document.Sections.Should().HaveCountGreaterThan(1);
		foreach (var section in document.Sections)
		{
			var input = MarkdownCorpus.EmbeddingText(section.Title, section.Heading, section.Text);
			input.Should().Be("Guide\nCurrent\n\n" + section.Text);
			fixture.Embedding.CountPassageTokens(input).Should().BeLessThanOrEqualTo(fixture.Embedding.MaxTokens);
		}
	}

	[TestMethod]
	[Description("Generated titles and headings are clipped only in bounded embedding input, not source metadata.")]
	public void When_ContextIsHuge_Then_PreparationIsBoundedAndMetadataRemainsRaw()
	{
		using var fixture = new TestCorpus();
		var title = string.Concat(Enumerable.Repeat("文😀", 10000));
		var heading = string.Concat(Enumerable.Repeat("章😀", 10000));
		var largestProbe = 0;
		fixture.Embedding.Counting = length => largestProbe = Math.Max(largestProbe, length);
		var document = fixture.Corpus.Parse("a.md", "# " + title + "\n## " + heading + "\nBody.");

		var section = document.Sections.Single();
		section.Title.Should().Be(title);
		section.Heading.Should().Be(heading);
		section.Text.Should().Be("Body.");
		var input = MarkdownCorpus.EmbeddingText(section.Title, section.Heading, section.Text);
		input.Should().EndWith("\n\nBody.");
		input.Length.Should().BeLessThan(80);
		foreach (var rune in input.EnumerateRunes()) { rune.Value.Should().NotBe(0xFFFD); }
		MarkdownCorpus.EmbeddingText(section.Title, section.Heading, section.Text).Should().Be(input);
		largestProbe.Should().BeLessThan(80);
	}

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

		document.Sections.Should().OnlyContain(section => embedding.CountPassageTokens(MarkdownCorpus.EmbeddingText(section.Title, section.Heading, section.Text)) <= window);
		document.Sections.Max(section => embedding.CountPassageTokens(MarkdownCorpus.EmbeddingText(section.Title, section.Heading, section.Text))).Should().Be(window);
		string.Concat(document.Sections.Select(section => section.Text)).Should().Be(text);
	}
	[TestMethod]
	[Description("Each chunk fits the actual embedding window while retaining source references and duplicate-heading identity.")]
	public void When_HeadingsRepeatAndParagraphsAreLong_Then_ChunksRemainBoundedAndAddressable()
	{
		using var fixture = new TestCorpus();
		var text = "# Guide\n\n## Console\n\n" + new string('a', 1300) + "😀\n\n## Console\n\nConnect to the console.\n";

		var document = fixture.Corpus.Parse("guide.md", text);

		document.Sections.Should().OnlyContain(section => fixture.Embedding.CountPassageTokens(MarkdownCorpus.EmbeddingText(section.Title, section.Heading, section.Text)) <= fixture.Embedding.MaxTokens);
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
	[Description("The bundled corpus exceeds the default twenty-passage candidate budget.")]
	public async Task When_TheBundledCorpusIsParsed_Then_ItExceedsTheDefaultCandidateWindow()
	{
		using var fixture = new TestCorpus();
		var settings = fixture.Settings with { Documents = Path.Combine(AppContext.BaseDirectory, "TestData", "documents") };
		var corpus = new MarkdownCorpus(settings, fixture.Embedding);
		var passages = 0;

		foreach (var path in corpus.Files())
		{
			var text = await corpus.ReadAsync(path, CancellationToken.None).ConfigureAwait(false);
			passages += corpus.Parse(corpus.RelativePath(path), text).Sections.Count;
		}

		passages.Should().BeGreaterThan(20);
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
