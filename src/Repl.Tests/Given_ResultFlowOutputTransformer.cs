using System.ComponentModel.DataAnnotations;

namespace Repl.Tests;

[TestClass]
public sealed class Given_ResultFlowOutputTransformer
{
	[TestMethod]
	[Description("A page rendered for the pager carries its table header on every page, declared with the columns it names, and no footer: the pager, not the transformer, drops a header that repeats the previous page's columns.")]
	public async Task When_RenderingAHumanPageForThePager_Then_ItsHeaderIsDeclaredAndItsFooterOmitted()
	{
		var transformer = CreateTransformer();
		var page = new ReplPage<ActivityRow>(
			[
				new ActivityRow(
					Id: 49,
					At: "2026-01-12 13:43Z",
					Area: "identity",
					Event: "validated",
					Summary: "identity batch 10 validated successfully"),
				new ActivityRow(
					Id: 50,
					At: "2026-01-12 13:50Z",
					Area: "billing",
					Event: "queued",
					Summary: "billing batch 10 queued successfully"),
			],
			new ReplPageInfo(
				Cursor: "48",
				NextCursor: "50",
				TotalCount: 250,
				PageSize: 2));

		var rendered = await ((ILayoutDeclaringOutputTransformer)transformer).RenderPageAsync(page, CancellationToken.None);

		var lines = rendered.Text.Split(Environment.NewLine);
		lines.Should().HaveCount(4, "the header, its separator and the two rows");
		lines[0].Should().MatchRegex(@"^#\s+At\s+Area\s+Event\s+Summary");
		rendered.Text.Should().NotContain("Showing ");
		rendered.Layout.HeaderLineCount.Should().Be(2);
		rendered.Layout.Columns.Should().Equal("#", "At", "Area", "Event", "Summary");
		rendered.Layout.FooterLineCount.Should().Be(0);
	}

	[TestMethod]
	[Description("A page rendered on its own keeps the footer asking to rerun for more, and declares it, so the pager strips exactly that line rather than any line that reads like one.")]
	public async Task When_RenderingAHumanPageOnItsOwn_Then_ItsFooterIsDeclared()
	{
		var transformer = CreateTransformer();
		var page = new ReplPage<ActivityRow>(
			[
				new ActivityRow(
					Id: 49,
					At: "2026-01-12 13:43Z",
					Area: "identity",
					Event: "validated",
					Summary: "identity batch 10 validated successfully"),
			],
			new ReplPageInfo(
				Cursor: null,
				NextCursor: "49",
				TotalCount: 250,
				PageSize: 1));

		var rendered = await ((ILayoutDeclaringOutputTransformer)transformer).RenderAsync(page, CancellationToken.None);

		rendered.Text.Split(Environment.NewLine)[^1].Should().StartWith("Showing 1 of 250.");
		rendered.Layout.HeaderLineCount.Should().Be(2);
		rendered.Layout.FooterLineCount.Should().Be(1);
	}

	private static HumanOutputTransformer CreateTransformer() =>
		new(() => new HumanRenderSettings(
			Width: 120,
			UseAnsi: false,
			Palette: new DefaultAnsiPaletteProvider().Create(ThemeMode.Dark)));

	[TestMethod]
	[Description("Human page footers never render unsafe cursor text directly.")]
	public async Task When_HumanPageFooterHasUnsafeCursor_Then_CursorIsNotRenderedVerbatim()
	{
		var transformer = new HumanOutputTransformer(
			() => new HumanRenderSettings(
				Width: 120,
				UseAnsi: false,
				Palette: new DefaultAnsiPaletteProvider().Create(ThemeMode.Dark)));
		var page = new ReplPage<ActivityRow>(
			[new ActivityRow(1, "2026-01-12 12:00Z", "ops", "queued", "queued")],
			new ReplPageInfo(
				Cursor: null,
				NextCursor: "abc\u001b[2J",
				TotalCount: 2,
				PageSize: 1));

		var output = await transformer.TransformAsync(page, CancellationToken.None);

		output.Should().NotContain("\u001b[2J");
		output.Should().Contain("cursor");
	}

	[TestMethod]
	[Description("Markdown page footers never render unsafe cursor text directly.")]
	public async Task When_MarkdownPageFooterHasUnsafeCursor_Then_CursorIsNotRenderedVerbatim()
	{
		var transformer = new MarkdownOutputTransformer();
		var page = new ReplPage<ActivityRow>(
			[new ActivityRow(1, "2026-01-12 12:00Z", "ops", "queued", "queued")],
			new ReplPageInfo(
				Cursor: null,
				NextCursor: "abc\u001b[2J",
				TotalCount: 2,
				PageSize: 1));

		var output = await transformer.TransformAsync(page, CancellationToken.None);

		output.Should().NotContain("\u001b[2J");
		output.Should().Contain("cursor");
	}

	private sealed record ActivityRow(
		[property: Display(Name = "#", Order = 0)] int Id,
		[property: Display(Name = "At", Order = 1)] string At,
		[property: Display(Name = "Area", Order = 2)] string Area,
		[property: Display(Name = "Event", Order = 3)] string Event,
		[property: Display(Name = "Summary", Order = 4)] string Summary);
}
