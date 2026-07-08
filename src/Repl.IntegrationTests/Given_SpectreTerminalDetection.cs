using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Repl.Spectre;
using Spectre.Console;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_SpectreTerminalDetection
{
	private const string AnsiIntroducer = "\u001b[";

	// Kept in sync by hand with Repl.Tests' TerminalTestEnvironments.Neutral (the two
	// test projects deliberately share no code).
	private static readonly (string Name, string? Value)[] NeutralAnsiEnvironment =
	[
		("NO_COLOR", null),
		("CLICOLOR_FORCE", null),
		("TERM", null),
		("TMUX", null),
		("WT_SESSION", null),
		("ConEmuANSI", null),
		("TERM_PROGRAM", null),
	];

	[TestMethod]
	[Description("On a host whose console cannot render ANSI (e.g. an IDE Run window), Spectre rendering follows the host's detection and emits no escape sequences — the raw-escapes half of issue #46. The host verdict is pinned through the resolver because the process console state (redirected or headless) differs between dev machines and CI runners.")]
	public void When_HostConsoleCannotRenderAnsi_Then_SpectreRendersWithoutAnsi()
	{
		using var env = new EnvironmentVariableScope(NeutralAnsiEnvironment);
		OutputOptions? configuredOutput = null;
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Options(o =>
		{
			o.Output.SetHostAnsiSupportResolver(static () => false);
			configuredOutput = o.Output;
		});
		sut.Map("wardrobe", () => new[]
		{
			new WardrobeRow("bib overalls", 42),
			new WardrobeRow("denim jacket", 7),
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["wardrobe", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("bib overalls");
		var gateVerdict = configuredOutput is { } configured
			? $"gate={TerminalAnsiCapability.IsAnsiCapableForTerminalSequences(configured)}, ansiEnabled={configured.IsAnsiEnabled()}"
			: "options not captured";
		var escapedText = output.Text.Replace(AnsiIntroducer, "<ESC>[", StringComparison.Ordinal);
		output.Text.Should().NotContain(
			AnsiIntroducer,
			because: $"a console that does not interpret ANSI must not receive escape sequences ({gateVerdict}, redirected={Console.IsOutputRedirected}, text=[{escapedText}])");
	}

	[TestMethod]
	[Description("Spectre's built-in CI profile enrichers (GitHub Actions and friends) force ANSI back on and must not override the host's detection: with GITHUB_ACTIONS set and an ANSI-incapable host, the render still carries no escape sequences.")]
	public void When_RunningUnderGitHubActions_Then_HostDetectionStillWins()
	{
		using var env = new EnvironmentVariableScope([.. NeutralAnsiEnvironment, ("GITHUB_ACTIONS", "true")]);
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Options(o => o.Output.SetHostAnsiSupportResolver(static () => false));
		sut.Map("wardrobe", () => new[]
		{
			new WardrobeRow("bib overalls", 42),
			new WardrobeRow("denim jacket", 7),
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["wardrobe", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().NotContain(
			AnsiIntroducer,
			because: "CI enrichers must not override the host's ANSI verdict");
	}

	[TestMethod]
	[Description("A hosted client advertising ANSI through capability flags (no AnsiSupport override) keeps Spectre colors: the same hosted capability fallback that drives marks and advanced progress applies to Spectre rendering.")]
	public void When_HostedClientAdvertisesAnsiViaCapabilities_Then_SpectreKeepsColors()
	{
		using var env = new EnvironmentVariableScope(NeutralAnsiEnvironment);
		var writer = new StringWriter();
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Map("wardrobe", () => new[]
		{
			new WardrobeRow("bib overalls", 42),
			new WardrobeRow("denim jacket", 7),
		});
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null);
		// A fixed size keeps Spectre off Console.WindowWidth, which headless CI consoles
		// report as 0.
		ReplSessionIO.WindowSize = (80, 24);
		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi;

		var exitCode = sut.Run(["wardrobe", "--no-logo"]);

		exitCode.Should().Be(0);
		writer.ToString().Should().Contain(AnsiIntroducer, because: "the client advertised ANSI, so colors must be preserved");
	}

	[TestMethod]
	[Description("Unicode box drawing follows the output sink's encoding: a handler-authored rounded table rendered into a sink that cannot carry ANY box-drawing glyph (rounded or square) is transliterated to ASCII — Spectre's own square safe border would hit the very same encoder fallback and ship '?' mojibake, the second half of issue #46.")]
	public void When_SessionWriterCannotCarryBoxDrawing_Then_BordersAreTransliteratedToAscii()
	{
		using var env = new EnvironmentVariableScope(NeutralAnsiEnvironment);
		var writer = new AsciiStringWriter();
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Map("wardrobe", (IAnsiConsole console) =>
		{
			var table = new Table().Border(TableBorder.Rounded)
				.AddColumn("Item").AddColumn("Quantity");
			table.AddRow("bib overalls", "42");
			console.Write(table);
			return Results.Success("Wardrobe displayed.");
		});
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null);
		// A fixed size keeps Spectre off Console.WindowWidth, which headless CI consoles
		// report as 0 — a zero-width profile would render the table as nothing.
		ReplSessionIO.WindowSize = (80, 24);

		var exitCode = sut.Run(["wardrobe", "--no-logo"]);

		exitCode.Should().Be(0);
		var text = writer.ToString();
		text.Should().Contain("bib overalls");
		text.Should().NotContain("╭", because: "the sink cannot carry the rounded glyph");
		text.Should().NotContain("┌", because: "the square safe-border glyph cannot survive an ASCII sink either — it would become '?' at the encoder");
		text.Should().Contain("+", because: "box drawing must be transliterated to ASCII when the sink cannot carry any box glyph");
	}

	[TestMethod]
	[Description("A hosted client advertising an absurd window size (int.MaxValue) must not flow into the Spectre profile: unclamped widths drive Spectre to allocate proportional buffers, a remote-triggered OOM vector.")]
	public void When_ClientAdvertisesAbsurdWindowSize_Then_ProfileDimensionsAreClamped()
	{
		using var session = ReplSessionIO.SetSession(new StringWriter(), TextReader.Null);
		ReplSessionIO.WindowSize = (int.MaxValue, int.MaxValue);

		var console = Repl.Spectre.SessionAnsiConsole.Create(outputOptions: null);

		console.Profile.Width.Should().BeLessThanOrEqualTo(10_000);
		console.Profile.Height.Should().BeLessThanOrEqualTo(1_000);
	}

	[TestMethod]
	[Description("With CI enrichers disabled, Interactive must be derived explicitly: under a redirected stdin (test hosts, CI) a handler calling console.Confirm() must fail fast instead of blocking on input that never comes.")]
	public void When_InputIsRedirected_Then_ProfileIsNotInteractive()
	{
		using var session = ReplSessionIO.SetSession(new StringWriter(), TextReader.Null);

		var console = Repl.Spectre.SessionAnsiConsole.Create(outputOptions: null);

		console.Profile.Capabilities.Interactive.Should().BeFalse(
			because: "a hosted session (and any redirected stdin) cannot answer interactive prompts");
	}

	[TestMethod]
	[Description("A hosted session reporting a zero window size (headless transports) must not produce a zero-width profile: Spectre renders nothing at width 0, so the resolver falls back to the default dimensions.")]
	public void When_SessionReportsZeroWindowSize_Then_BorderedTableStillRenders()
	{
		using var env = new EnvironmentVariableScope(NeutralAnsiEnvironment);
		var writer = new StringWriter();
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Map("wardrobe", (IAnsiConsole console) =>
		{
			var table = new Table().Border(TableBorder.Rounded)
				.AddColumn("Item").AddColumn("Quantity");
			table.AddRow("bib overalls", "42");
			console.Write(table);
			return Results.Success("Wardrobe displayed.");
		});
		using var session = ReplSessionIO.SetSession(writer, TextReader.Null);
		ReplSessionIO.WindowSize = (0, 0);

		var exitCode = sut.Run(["wardrobe", "--no-logo"]);

		exitCode.Should().Be(0);
		writer.ToString().Should().Contain("bib overalls", because: "a zero-size report must fall back to usable dimensions instead of rendering nothing");
	}

	[TestMethod]
	[Description("The container wiring the interaction handler depends on: OutputOptions must be registered and be the same instance the app configures, otherwise the prompt path silently falls back to Spectre-side detection.")]
	public void When_ResolvingOutputOptionsFromTheContainer_Then_ConfiguredInstanceIsReturned()
	{
		OutputOptions? configuredOutput = null;
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Options(o => configuredOutput = o.Output);

		var resolved = sut.Services.GetService(typeof(OutputOptions)) as OutputOptions;

		resolved.Should().NotBeNull().And.BeSameAs(configuredOutput);
	}

	[TestMethod]
	[Description("External-DI hosting (the AddRepl pattern): a provider that carries the ReplApp but not the framework-registered OutputOptions must still honor the app's terminal detection for the injected IAnsiConsole — the factory falls back to the app's own container instead of silently reverting to Spectre-side detection.")]
	public void When_ResolvingAnsiConsoleFromExternalContainer_Then_AppTerminalDetectionStillApplies()
	{
		using var env = new EnvironmentVariableScope(NeutralAnsiEnvironment);
		var app = ReplApp.Create();
		app.Options(o => o.Output.AnsiMode = Rendering.AnsiMode.Always);
		var services = new ServiceCollection().AddSpectreConsole();
		services.AddSingleton(app);
		using var external = services.BuildServiceProvider();

		var console = external.GetRequiredService<IAnsiConsole>();

		console.Profile.Capabilities.Ansi.Should().BeTrue(
			because: "the app forces ANSI on; a console resolved from the externally managed container must inherit that verdict");
	}

	[TestMethod]
	[Description("Spectre console options are per app: a second app configuring Unicode=false must not strip box-drawing from an earlier app whose sink carries Unicode — the same contamination class the OutputOptions flow already prevents.")]
	public void When_AnotherAppDisablesUnicode_Then_FirstAppKeepsItsBoxDrawing()
	{
		using var env = new EnvironmentVariableScope(NeutralAnsiEnvironment);
		var writer = new StringWriter();
		var sut = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole();
		sut.Map("wardrobe", (IAnsiConsole console) =>
		{
			var table = new Table().Border(TableBorder.Rounded)
				.AddColumn("Item").AddColumn("Quantity");
			table.AddRow("bib overalls", "42");
			console.Write(table);
			return Results.Success("Wardrobe displayed.");
		});

		// A second app in the same process opts out of Unicode AFTER the first app
		// was configured; the first app must be unaffected.
		_ = ReplApp.Create(services => services.AddSpectreConsole())
			.UseSpectreConsole(o => o.Unicode = false);

		using var session = ReplSessionIO.SetSession(writer, TextReader.Null);
		ReplSessionIO.WindowSize = (80, 24);
		var exitCode = sut.Run(["wardrobe", "--no-logo"]);

		exitCode.Should().Be(0);
		writer.ToString().Should().Contain("╭", because: "the sibling app's Unicode opt-out must not leak into this app");
	}

	[TestMethod]
	[Description("The framework exposes its box-drawing verdict (SpectreTerminalDetection) so diagnostics commands display it instead of re-implementing the probe: an ASCII session writer reports Ascii — the same verdict that activates the transliterating writer.")]
	public void When_QueryingBoxDrawingSupport_Then_FrameworkVerdictIsExposed()
	{
		using var session = ReplSessionIO.SetSession(new AsciiStringWriter(), TextReader.Null);

		var support = SpectreTerminalDetection.CurrentBoxDrawingSupport;

		support.Should().Be(BoxDrawingSupport.Ascii);
	}

	private sealed record WardrobeRow(string Item, int Quantity);

	// A session writer whose declared encoding cannot carry box-drawing glyphs, standing in
	// for a legacy-codepage console attached to a hosted transport.
	private sealed class AsciiStringWriter : StringWriter
	{
		public override Encoding Encoding => Encoding.ASCII;
	}
}
