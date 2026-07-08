using System.Text;
using Repl.Spectre;
using Spectre.Console;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_SpectreTerminalDetection
{
	// Built from the char code so no raw control byte lives in the source file.
	private static readonly string AnsiIntroducer = $"{(char)0x1b}[";

	private static readonly (string Name, string? Value)[] NeutralAnsiEnvironment =
	[
		("NO_COLOR", null),
		("CLICOLOR_FORCE", null),
		("TERM", null),
		("TMUX", null),
		("WT_SESSION", null),
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
	[Description("Unicode box drawing follows the output sink's encoding: a handler-authored bordered table rendered into a session writer that cannot carry box-drawing glyphs falls back to Spectre's ASCII-safe border — the mojibake half of issue #46.")]
	public void When_SessionWriterCannotCarryBoxDrawing_Then_BorderedTableFallsBackToAscii()
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
		text.Should().NotContain("╭", because: "the ASCII sink cannot carry box-drawing glyphs; Spectre's safe border must apply");
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

		resolved.Should().BeSameAs(configuredOutput);
	}

	private sealed record WardrobeRow(string Item, int Quantity);

	// A session writer whose declared encoding cannot carry box-drawing glyphs, standing in
	// for a legacy-codepage console attached to a hosted transport.
	private sealed class AsciiStringWriter : StringWriter
	{
		public override Encoding Encoding => Encoding.ASCII;
	}
}
