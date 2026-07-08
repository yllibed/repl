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
			because: $"a console that does not interpret ANSI must not receive escape sequences ({gateVerdict}, redirected={Console.IsOutputRedirected}, trace={Repl.Spectre.SessionAnsiConsole.LastDetectionTrace}, text=[{escapedText}])");
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

	private sealed record WardrobeRow(string Item, int Quantity);

	// A session writer whose declared encoding cannot carry box-drawing glyphs, standing in
	// for a legacy-codepage console attached to a hosted transport.
	private sealed class AsciiStringWriter : StringWriter
	{
		public override Encoding Encoding => Encoding.ASCII;
	}
}
