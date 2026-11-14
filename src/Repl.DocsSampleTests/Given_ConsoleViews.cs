using DocsMcpSample;
using DocsMcpSample.Search;
using Spectre.Console;

namespace Repl.DocsSampleTests;

[TestClass]
public sealed class Given_ConsoleViews
{
	[TestMethod]
	[Description("A console reader can copy the offset continuation command from a truncated fetch, even with ANSI enabled.")]
	public async Task When_AFetchHasMoreText_Then_TheConsoleShowsHowToContinue()
	{
		var document = new FetchedDocument(Id: "section", Source: "guide.md", Title: "Guide", Text: "Text", Truncated: true, Generation: "generation", Offset: 0, NextOffset: 100, NextId: null, DocumentId: "doc");

		var output = await RenderAsync(document).ConfigureAwait(false);

		output.Should().Contain("docs fetch --id section --offset 100");
		output.Should().NotContain("fetch a section for a focused extract");
	}

	[TestMethod]
	[Description("A terminal section page exposes its sibling as a fetch command rather than leaving the reader at a dead end.")]
	public async Task When_AFetchHasANextPart_Then_TheConsoleShowsItsIdentifier()
	{
		var document = new FetchedDocument(Id: "section", Source: "guide.md", Title: "Guide", Text: "Text", Truncated: false, Generation: "generation", Offset: 0, NextOffset: null, NextId: "next-section", DocumentId: "doc");

		(await RenderAsync(document).ConfigureAwait(false)).Should().Contain("docs fetch --id next-section");
	}

	private static async Task<string> RenderAsync(FetchedDocument document)
	{
		var writer = new StringWriter();
		await using var owner = writer.ConfigureAwait(false);
		var console = AnsiConsole.Create(new AnsiConsoleSettings
		{
			Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.Yes,
			ColorSystem = ColorSystemSupport.Standard, Interactive = InteractionSupport.No,
		});
		console.Profile.Width = 160;
		console.Write(ConsoleViews.Document(document));
		var output = writer.ToString();
		output.Should().Contain("From the root prompt (inside docs, enter .. first):");
		return output;
	}
}
