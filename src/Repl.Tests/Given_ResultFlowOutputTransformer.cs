using System.ComponentModel.DataAnnotations;

namespace Repl.Tests;

[TestClass]
public sealed class Given_ResultFlowOutputTransformer
{
	[TestMethod]
	[Description("Result-flow continuation payloads render table rows only so pagers do not receive repeated headers or page footers.")]
	public async Task When_RenderingHumanContinuationPage_Then_HeaderAndFooterAreOmitted()
	{
		var transformer = new HumanOutputTransformer(
			() => new HumanRenderSettings(
				Width: 120,
				UseAnsi: false,
				Palette: new DefaultAnsiPaletteProvider().Create(ThemeMode.Dark)));
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

		var output = await ((IResultFlowOutputTransformer)transformer).TransformPageAsync(
			page,
			ResultFlowPageRenderMode.Continuation,
			CancellationToken.None);

		output.Should().NotContain("#");
		output.Should().NotContain("---");
		output.Should().NotContain("Showing ");
		output.Should().Contain("49");
		output.Should().Contain("identity batch 10 validated successfully");
		output.Should().Contain("50");
		output.Should().Contain("billing batch 10 queued successfully");
	}

	[TestMethod]
	[Description("Result-flow initial payloads keep the table header so the first page remains readable.")]
	public async Task When_RenderingHumanInitialPage_Then_HeaderIsIncluded()
	{
		var transformer = new HumanOutputTransformer(
			() => new HumanRenderSettings(
				Width: 120,
				UseAnsi: false,
				Palette: new DefaultAnsiPaletteProvider().Create(ThemeMode.Dark)));
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

		var output = await ((IResultFlowOutputTransformer)transformer).TransformPageAsync(
			page,
			ResultFlowPageRenderMode.Initial,
			CancellationToken.None);

		output.Should().Contain("#");
		output.Should().Contain("At");
		output.Should().NotContain("Showing ");
	}

	private sealed record ActivityRow(
		[property: Display(Name = "#", Order = 0)] int Id,
		[property: Display(Name = "At", Order = 1)] string At,
		[property: Display(Name = "Area", Order = 2)] string Area,
		[property: Display(Name = "Event", Order = 3)] string Event,
		[property: Display(Name = "Summary", Order = 4)] string Summary);
}
