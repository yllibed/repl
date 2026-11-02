using Spectre.Console;
using Repl.Rendering;

namespace Repl.SpectreTests;

[TestClass]
public sealed class Given_SpectreViews
{
	[TestMethod]
	[Description("An application view replaces reflection for its result type without replacing the Spectre transformer.")]
	public async Task When_AViewIsRegistered_Then_ItRendersTheResult()
	{
		var options = new SpectreConsoleOptions();
		options.AddView<Status>(status => new Text($"State: {status.Ready}"));
		var transformer = CreateTransformer(options);

		var output = await transformer.TransformAsync(new Status(Ready: true)).ConfigureAwait(false);

		output.Should().Be("State: True");
		transformer.SupportsInteractivePaging.Should().BeTrue();
		transformer.MimeType.Should().Be("text/plain");
	}

	[TestMethod]
	[Description("A view for a base class cannot silently change the presentation of a different runtime type.")]
	public async Task When_OnlyABaseTypeHasAView_Then_ADerivedResultUsesTheDefaultRendering()
	{
		var options = new SpectreConsoleOptions();
		options.AddView<BaseStatus>(_ => new Text("custom base view"));
		var transformer = CreateTransformer(options);

		var output = await transformer.TransformAsync(new DetailedStatus("Published")).ConfigureAwait(false);

		output.Should().Contain("Published").And.NotContain("custom base view");
	}

	[TestMethod]
	[Description("Views belong to their application options; configuring another instance cannot alter an existing renderer.")]
	public async Task When_TwoApplicationsHaveDifferentViews_Then_TheirRenderingIsIsolated()
	{
		var first = new SpectreConsoleOptions();
		var second = new SpectreConsoleOptions();
		first.AddView<Status>(_ => new Text("first"));
		second.AddView<Status>(_ => new Text("second"));

		var outputs = await Task.WhenAll(
			CreateTransformer(first).TransformAsync(new Status(Ready: true)).AsTask(),
			CreateTransformer(second).TransformAsync(new Status(Ready: true)).AsTask()).ConfigureAwait(false);

		outputs.Should().Equal("first", "second");
	}

	[TestMethod]
	[Description("A later registration deliberately replaces the earlier view for that exact type.")]
	public async Task When_ATypeIsRegisteredAgain_Then_TheLastViewWins()
	{
		var options = new SpectreConsoleOptions();
		options.AddView<Status>(_ => new Text("old"));
		options.AddView<Status>(_ => new Text("replacement"));

		var output = await CreateTransformer(options).TransformAsync(new Status(Ready: true)).ConfigureAwait(false);

		output.Should().Be("replacement");
	}

	[TestMethod]
	[Description("Null command results are empty output and do not invoke a registered view.")]
	public async Task When_TheResultIsNull_Then_NoViewIsInvoked()
	{
		var options = new SpectreConsoleOptions();
		options.AddView<Status>(_ => throw new InvalidOperationException("Must not render null"));

		var output = await CreateTransformer(options).TransformAsync(value: null).ConfigureAwait(false);

		output.Should().BeEmpty();
	}

	[TestMethod]
	[Description("An absent view delegate is rejected at configuration time rather than during a command.")]
	public void When_TheViewDelegateIsNull_Then_RegistrationFails()
	{
		var options = new SpectreConsoleOptions();

		var register = () => options.AddView<Status>(null!);

		register.Should().Throw<ArgumentNullException>().WithParameterName("view");
	}

	[TestMethod]
	[Description("A broken view reports its registered type rather than failing inside Spectre with an unrelated null error.")]
	public async Task When_AViewReturnsNull_Then_TheErrorIdentifiesTheView()
	{
		var options = new SpectreConsoleOptions();
		options.AddView<Status>(_ => null!);
		var transformer = CreateTransformer(options);

		var render = async () => await transformer.TransformAsync(new Status(Ready: true)).ConfigureAwait(false);

		await render.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Status*returned null*").ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Page envelopes keep their native table layout and footer even if a view is registered for their CLR type or item type.")]
	public async Task When_APageIsRendered_Then_ItsLayoutAndFooterRemainNative()
	{
		var options = new SpectreConsoleOptions();
		options.AddView<ReplPage<Status>>(_ => new Text("custom page"));
		options.AddView<Status>(_ => new Text("custom item"));
		var transformer = CreateTransformer(options);
		var page = new ReplPage<Status>([new Status(Ready: true)],
			new ReplPageInfo(Cursor: null, NextCursor: null, TotalCount: 1, PageSize: 1));

		var body = await transformer.RenderPageAsync(page).ConfigureAwait(false);
		var full = await transformer.RenderAsync(page).ConfigureAwait(false);

		body.Layout.HeaderLineCount.Should().Be(1);
		body.Text.Should().Contain("Ready").And.NotContain("custom");
		full.Text.Should().StartWith(body.Text);
		full.Layout.FooterLineCount.Should().Be(1);
	}

	private static SpectreHumanOutputTransformer CreateTransformer(SpectreConsoleOptions options) =>
		new(() => new HumanRenderSettings(Width: 80, UseAnsi: false, Palette: new DefaultAnsiPaletteProvider().Create(ThemeMode.Dark)),
			new OutputOptions { AnsiMode = AnsiMode.Never }, options);

	private sealed record Status(bool Ready);
	private abstract record BaseStatus;
	private sealed record DetailedStatus(string State) : BaseStatus;
}
