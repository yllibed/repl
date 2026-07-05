using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_InteractiveSession_ShellIntegrationMarks
{
	private static readonly (string Name, string? Value)[] NeutralTerminalEnvironment =
	[
		("TMUX", null),
		("TERM", null),
		("WT_SESSION", null),
		("ConEmuANSI", null),
		("TERM_PROGRAM", null),
	];

	[TestMethod]
	[Description("A successful interactive command is wrapped by the full lifecycle: prompt start, input start, output start, command output, and command end with exit code 0, then the next prompt starts a new cycle.")]
	public void When_CommandSucceeds_Then_PromptInputOutputAndEndMarksWrapTheCommand()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "ping\rexit\r");

		var promptStart = raw.IndexOf("]133;A", StringComparison.Ordinal);
		var inputStart = raw.IndexOf("]133;B", StringComparison.Ordinal);
		var outputStart = raw.IndexOf("]133;C", StringComparison.Ordinal);
		var commandOutput = raw.IndexOf("pong", StringComparison.Ordinal);
		var commandEnd = raw.IndexOf("]133;D;0", StringComparison.Ordinal);
		promptStart.Should().BeGreaterThanOrEqualTo(0);
		inputStart.Should().BeGreaterThan(promptStart);
		outputStart.Should().BeGreaterThan(inputStart);
		commandOutput.Should().BeGreaterThan(outputStart);
		commandEnd.Should().BeGreaterThan(commandOutput);
		raw.IndexOf("]133;A", commandEnd, StringComparison.Ordinal).Should().BeGreaterThan(commandEnd);
	}

	[TestMethod]
	[Description("A command returning an error result reports exit code 1 in the command-end mark so terminals decorate the command as failed.")]
	public void When_CommandReturnsError_Then_CommandEndReportsExitCodeOne()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("boom", () => Results.Error("boom-failed", "nope"));
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "boom\rexit\r");

		raw.Should().Contain("]133;D;1");
	}

	[TestMethod]
	[Description("An unknown command resolves to a route-resolution failure and reports exit code 1 in the command-end mark.")]
	public void When_UnknownCommandIsEntered_Then_CommandEndReportsExitCodeOne()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "zorglub\rexit\r");

		raw.Should().Contain("]133;D;1");
	}

	[TestMethod]
	[Description("Ambient commands such as help run inside the same command lifecycle: their output lands between output-start and a successful command-end mark.")]
	public void When_HelpAmbientCommandRuns_Then_MarksWrapHelpOutput()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong").WithDescription("Answers with pong.");
		var harness = new TerminalHarness(cols: 80, rows: 24);

		var raw = RunInteractiveSession(harness, sut, "help\rexit\r");

		var outputStart = raw.IndexOf("]133;C", StringComparison.Ordinal);
		var helpOutput = raw.IndexOf("Answers with pong.", StringComparison.Ordinal);
		var commandEnd = raw.IndexOf("]133;D;0", StringComparison.Ordinal);
		outputStart.Should().BeGreaterThanOrEqualTo(0);
		helpOutput.Should().BeGreaterThan(outputStart);
		commandEnd.Should().BeGreaterThan(helpOutput);
	}

	[TestMethod]
	[Description("Committing an empty line aborts the cycle: command-end is reported without an exit code and no output-start mark is emitted before it.")]
	public void When_EmptyLineIsCommitted_Then_CommandEndsWithoutExitCodeAndWithoutOutputMark()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "\rexit\r");

		var firstCommandEnd = raw.IndexOf("]133;D", StringComparison.Ordinal);
		var firstOutputStart = raw.IndexOf("]133;C", StringComparison.Ordinal);
		firstCommandEnd.Should().BeGreaterThanOrEqualTo(0);
		raw.Substring(firstCommandEnd, 8).Should().NotContain("D;");
		firstOutputStart.Should().BeGreaterThan(firstCommandEnd, because: "the only output-start mark belongs to the later exit command");
	}

	[TestMethod]
	[Description("Escaping at an empty prompt abandons the cycle: command-end is reported without an exit code, matching the FinalTerm aborted-command form.")]
	public void When_EscapeAbandonsThePrompt_Then_CommandEndsWithoutExitCode()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "\u001bexit\r");

		var firstCommandEnd = raw.IndexOf("]133;D", StringComparison.Ordinal);
		firstCommandEnd.Should().BeGreaterThanOrEqualTo(0);
		raw.Substring(firstCommandEnd, 8).Should().NotContain("D;");
	}

	[TestMethod]
	[Description("The exit ambient command closes its own cycle: the final command-end mark reports exit code 0 and no mark follows it.")]
	public void When_ExitCommandRuns_Then_FinalCommandEndIsEmittedBeforeSessionEnds()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "exit\r");

		var commandEnd = raw.IndexOf("]133;D;0", StringComparison.Ordinal);
		commandEnd.Should().BeGreaterThanOrEqualTo(0);
		raw.IndexOf("]133;", commandEnd + 1, StringComparison.Ordinal).Should().Be(-1);
	}

	[TestMethod]
	[Description("A handler cancelled mid-command keeps the Cancelled. message and reports exit code 130 (128+SIGINT), the shell convention terminals interpret as an interrupted command.")]
	public void When_HandlerThrowsOperationCanceled_Then_CancelledLineIsPrintedAndExitCodeIs130()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("boom", string () => throw new OperationCanceledException());
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "boom\rexit\r");

		raw.Should().Contain("Cancelled.");
		raw.Should().Contain("]133;D;130");
	}

	[TestMethod]
	[Description("A session reporting a VS Code terminal identity selects the OSC 633 backend, which reports the committed command line with 633;E so command detection is independent of screen scraping.")]
	public void When_VsCodeTerminalIsDetected_Then_CommandLineIsReportedWithOsc633E()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp(ShellIntegrationMode.Auto);
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "ping\rexit\r", terminalIdentity: "vscode");

		var inputStart = raw.IndexOf("]633;B", StringComparison.Ordinal);
		var commandLine = raw.IndexOf("]633;E;ping", StringComparison.Ordinal);
		var outputStart = raw.IndexOf("]633;C", StringComparison.Ordinal);
		inputStart.Should().BeGreaterThanOrEqualTo(0);
		commandLine.Should().BeGreaterThan(inputStart);
		outputStart.Should().BeGreaterThan(commandLine);
		raw.Should().NotContain("]133;");
	}

	[TestMethod]
	[Description("A failed completion ambient command (complete without --target) reports exit code 1 in the command-end mark instead of decorating the failure as success.")]
	public void When_CompleteAmbientCommandFails_Then_CommandEndReportsExitCodeOne()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "complete\rexit\r");

		raw.Should().Contain("Error: complete requires --target");
		raw.Should().Contain("]133;D;1");
	}

	[TestMethod]
	[Description("An ambient help invocation that fails to render (unknown output format) reports exit code 1 in the command-end mark, matching the non-ambient --help path.")]
	public void When_HelpAmbientCommandFailsToRender_Then_CommandEndReportsExitCodeOne()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "help --output:bogus\rexit\r");

		raw.Should().Contain("]133;D;1");
	}

	[TestMethod]
	[Description("A protocol-passthrough command run interactively gets no output-start or command-end marks: OSC bytes must never precede or trail a protocol payload on the same stream.")]
	public void When_ProtocolPassthroughCommandRunsInteractively_Then_NoOutputMarksWrapTheProtocolStream()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("serve", () => "protocol-payload").AsProtocolPassthrough();
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "serve\rexit\r");

		raw.Should().Contain("protocol-payload");
		CountOccurrences(raw, "]133;C").Should().Be(1, because: "only the exit cycle may open an output region");
		CountOccurrences(raw, "]133;D").Should().Be(1, because: "no command-end mark may trail the protocol payload");
		raw.IndexOf("]133;C", StringComparison.Ordinal)
			.Should().BeGreaterThan(
				raw.IndexOf("protocol-payload", StringComparison.Ordinal),
				because: "the only output-start mark belongs to the later exit command");
	}

	[TestMethod]
	[Description("A dispatch that throws (history with a non-numeric --limit) still closes the lifecycle with a failed command-end mark so the terminal never keeps an unterminated command segment.")]
	public void When_AmbientCommandThrows_Then_CommandEndStillReportsFailure()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "history --limit abc\r", swallowRunExceptions: true);

		raw.Should().Contain("]133;D;1");
	}

	[TestMethod]
	[Description("Without UseTerminalIntegration the interactive loop emits no shell-integration marks at all: the feature is opt-in.")]
	public void When_IntegrationIsNotConfigured_Then_NoMarksAreEmitted()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", "test-session"),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "ping\rexit\r");

		raw.Should().Contain("pong");
		raw.Should().NotContain("]133;");
		raw.Should().NotContain("]633;");
	}

	[TestMethod]
	[Description("Interaction events written by a handler (status lines) do not open nested lifecycle cycles: exactly one prompt-start mark per loop iteration.")]
	public void When_HandlerWritesInteractionEvents_Then_OnlyLoopPromptsEmitMarks()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var sut = CreateMarkedApp();
		sut.Map("work", async (IReplInteractionChannel channel) =>
		{
			await channel.WriteStatusAsync("working on it", CancellationToken.None).ConfigureAwait(false);
			return "done";
		});
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "work\rexit\r");

		raw.Should().Contain("working on it");
		CountOccurrences(raw, "]133;A").Should().Be(2, because: "only the work and exit prompt cycles may open a lifecycle");
	}

	private static ReplApp CreateMarkedApp(ShellIntegrationMode mode = ShellIntegrationMode.Always)
	{
		var sut = ReplApp.Create()
			.UseDefaultInteractive()
			.UseTerminalIntegration(options => options.ShellIntegration = mode);
		sut.Options(options => options.Output.AnsiMode = AnsiMode.Always);
		return sut;
	}

	private static string RunInteractiveSession(
		TerminalHarness harness,
		ReplApp sut,
		string typedInput,
		string? terminalIdentity = null,
		bool swallowRunExceptions = false)
	{
		var keyReader = new FakeKeyReader(typedInput.Select(ToKeyInfo).ToArray());
		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = (harness.Cols, harness.Rows);
			ReplSessionIO.AnsiSupport = true;
			ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.VtInput;
			if (terminalIdentity is not null)
			{
				ReplSessionIO.TerminalIdentity = terminalIdentity;
			}

			try
			{
				_ = sut.Run([]);
			}
			catch when (swallowRunExceptions)
			{
				// The test asserts on the marks emitted before the crash propagated.
			}

			return harness.RawOutput;
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	private static ConsoleKeyInfo ToKeyInfo(char ch) =>
		ch switch
		{
			'\r' => new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false),
			'\u001b' => new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, shift: false, alt: false, control: false),
			_ => new ConsoleKeyInfo(ch, CharToConsoleKey(ch), shift: false, alt: false, control: false),
		};

	private static ConsoleKey CharToConsoleKey(char ch) =>
		char.IsAsciiLetter(ch)
			? ConsoleKey.A + (char.ToUpperInvariant(ch) - 'A')
			: ConsoleKey.Spacebar;

	private static int CountOccurrences(string text, string needle)
	{
		var count = 0;
		var index = 0;
		while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += needle.Length;
		}

		return count;
	}
}
