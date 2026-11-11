using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

/// <summary>
/// Consumers that write terminal control sequences (screen clearing, shell-integration marks) or report the
/// terminal's ability to take them: forced colour must not open them, and a terminal that may be driven must.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Given_ForceColorConsumers
{
	private const string ClearScreen = "\u001b[2J";

	[TestMethod]
	[Description("A screen clear on a terminal still writes the clear sequence: the control for the redirected case below.")]
	public async Task When_ScreenIsClearedOnATerminal_Then_TheClearSequenceIsWritten()
	{
		using var env = TerminalTestEnvironments.NeutralWith();
		using var writer = new StringWriter();

		await PresentClearScreenAsync(writer, isStreamRedirected: false);

		writer.ToString().Should().Contain(ClearScreen);
	}

	[TestMethod]
	[DataRow("FORCE_COLOR")]
	[DataRow("CLICOLOR_FORCE")]
	[Description("Colour forced onto a redirected stream must not clear the screen: the sequence would land in the file or in the next program's input.")]
	public async Task When_ColourIsForcedOnARedirectedStream_Then_ClearingTheScreenWritesNothing(string variable)
	{
		using var env = TerminalTestEnvironments.NeutralWith((variable, "1"));
		using var writer = new StringWriter();

		await PresentClearScreenAsync(writer, isStreamRedirected: true);

		writer.ToString().Should().NotContain("\u001b");
	}

	[TestMethod]
	[DataRow("NO_COLOR", "1")]
	[DataRow("TERM", "dumb")]
	[Description("On a terminal, FORCE_COLOR turns colour on despite NO_COLOR or TERM=dumb, but those keep their veto on the screen clear.")]
	public async Task When_ColourIsForcedDespiteAnOptOutOnATerminal_Then_ClearingTheScreenWritesNothing(string variable, string value)
	{
		using var env = TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1"), (variable, value));
		using var writer = new StringWriter();

		await PresentClearScreenAsync(writer, isStreamRedirected: false);

		writer.ToString().Should().NotContain(ClearScreen);
	}

	[TestMethod]
	[DataRow("FORCE_COLOR")]
	[DataRow("CLICOLOR_FORCE")]
	[Description("ITerminalInfo.IsAnsiSupported tells a custom prompt handler whether it may drive the terminal. With colour forced it follows the console's redirection (redirected under the test runner), not the forced colour, while the palette follows the colour.")]
	public void When_ColourIsForced_Then_TerminalInfoFollowsTheRedirectionButKeepsThePalette(string variable)
	{
		using var env = TerminalTestEnvironments.NeutralWith((variable, "1"));
		var info = new ConsoleTerminalInfo(new OutputOptions());

		info.IsAnsiSupported.Should().Be(
			!Console.IsOutputRedirected,
			because: "forcing colour asks for colour, not for control of a redirected output");
		info.Palette.Should().NotBeNull(because: "the palette is styling, which forcing colour does ask for");
	}

	[TestMethod]
	[Description("A hosted client advertising ANSI only through capability flags may be driven, and then also gets a palette: a handler that may draw is never left without colours the session supports.")]
	public void When_AHostedClientAdvertisesAnsiThroughCapabilities_Then_TerminalInfoReportsAnsiAndAPalette()
	{
		using var env = TerminalTestEnvironments.NeutralWith();
		using var session = ReplSessionIO.SetSession(output: TextWriter.Null, input: TextReader.Null);
		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi;
		var info = new ConsoleTerminalInfo(new OutputOptions());

		info.IsAnsiSupported.Should().BeTrue();
		info.Palette.Should().NotBeNull();
	}

	[TestMethod]
	[Description("With colour forced, shell-integration marks follow the console's redirection (redirected under the test runner): on redirected output they stay off and the status names the redirection, as it does when CLICOLOR_FORCE forced the colour.")]
	public async Task When_ColourIsForced_Then_ShellMarksFollowTheRedirection()
	{
		using var env = TerminalTestEnvironments.NeutralWith(("FORCE_COLOR", "1"), ("WT_SESSION", "test"));
		var emitter = ShellIntegrationMarkEmitter.Create(
			new TerminalIntegrationOptions { ShellIntegration = ShellIntegrationMode.Auto },
			new OutputOptions());

		await emitter.WritePromptStartAsync();

		emitter.LastGate.Should().Be(
			Console.IsOutputRedirected ? ShellIntegrationGate.OutputRedirected : ShellIntegrationGate.Enabled);
	}

	[TestMethod]
	[Description("A hosted client advertising ANSI only through capability flags may be driven, as it already got shell-integration marks: its screen is cleared even though the server's own console is redirected.")]
	public async Task When_AHostedClientAdvertisesAnsiThroughCapabilities_Then_ItsScreenIsCleared()
	{
		using var env = TerminalTestEnvironments.NeutralWith();
		using var writer = new StringWriter();
		using var session = ReplSessionIO.SetSession(output: writer, input: TextReader.Null);
		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi;

		await PresentClearScreenAsync(writer, isStreamRedirected: true, isSessionWriter: true);

		writer.ToString().Should().Contain(ClearScreen);
	}

	private static async Task PresentClearScreenAsync(TextWriter writer, bool isStreamRedirected, bool isSessionWriter = false)
	{
		var presenter = new ConsoleReplInteractionPresenter(
			new InteractionOptions(),
			new OutputOptions(),
			_ => new FeedbackTarget(writer, IsError: false, isSessionWriter, isStreamRedirected));

		await presenter.PresentAsync(new ReplClearScreenEvent(), CancellationToken.None).ConfigureAwait(false);
	}
}
