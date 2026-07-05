using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ShellIntegrationMarkEmitter
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
	[Description("Always mode emits the full OSC 133 lifecycle in order: prompt start (A), input start (B), output start (C), command end with exit code (D).")]
	public async Task When_ModeAlways_AndAnsiSession_Then_LifecycleMarksAreEmittedInOrder()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Always);

		await emitter.WritePromptStartAsync();
		await emitter.WriteInputStartAsync();
		await emitter.WriteOutputStartAsync();
		await emitter.WriteCommandEndAsync(exitCode: 0);

		var raw = harness.RawOutput;
		var promptStart = raw.IndexOf("]133;A", StringComparison.Ordinal);
		var inputStart = raw.IndexOf("]133;B", StringComparison.Ordinal);
		var outputStart = raw.IndexOf("]133;C", StringComparison.Ordinal);
		var commandEnd = raw.IndexOf("]133;D;0", StringComparison.Ordinal);
		promptStart.Should().BeGreaterThanOrEqualTo(0);
		inputStart.Should().BeGreaterThan(promptStart);
		outputStart.Should().BeGreaterThan(inputStart);
		commandEnd.Should().BeGreaterThan(outputStart);
	}

	[TestMethod]
	[Description("Never mode suppresses every mark even on a capable ANSI session.")]
	public async Task When_ModeNever_Then_NoMarksAreEmitted()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", "test-session"),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Never);

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().NotContain("]133;");
		harness.RawOutput.Should().NotContain("]633;");
	}

	[TestMethod]
	[Description("Auto mode emits OSC 133 marks when Windows Terminal is detected through WT_SESSION.")]
	public async Task When_ModeAuto_AndWindowsTerminalDetected_Then_Osc133MarksAreEmitted()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", "test-session"),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().Contain("]133;A");
		harness.RawOutput.Should().Contain("]133;D;0");
		harness.RawOutput.Should().NotContain("]633;");
	}

	[TestMethod]
	[Description("Auto mode selects the OSC 633 backend under VS Code (TERM_PROGRAM=vscode) and reports the command line with 633;E between input start and output start.")]
	public async Task When_ModeAuto_AndVsCodeDetected_Then_Osc633MarksAreEmittedIncludingCommandLine()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", null),
			("ConEmuANSI", null),
			("TERM_PROGRAM", "vscode"));
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);

		await emitter.WritePromptStartAsync();
		await emitter.WriteInputStartAsync();
		await emitter.WriteCommandLineAsync("ping");
		await emitter.WriteOutputStartAsync();
		await emitter.WriteCommandEndAsync(exitCode: 0);

		var raw = harness.RawOutput;
		raw.Should().NotContain("]133;");
		var inputStart = raw.IndexOf("]633;B", StringComparison.Ordinal);
		var commandLine = raw.IndexOf("]633;E;ping", StringComparison.Ordinal);
		var outputStart = raw.IndexOf("]633;C", StringComparison.Ordinal);
		inputStart.Should().BeGreaterThanOrEqualTo(0);
		commandLine.Should().BeGreaterThan(inputStart);
		outputStart.Should().BeGreaterThan(commandLine);
		raw.Should().Contain("]633;D;0");
	}

	[TestMethod]
	[Description("Auto mode stays silent under tmux even when the outer terminal is capable: mark positioning is unreliable through multiplexers.")]
	public async Task When_ModeAuto_AndTmuxDetected_Then_NoMarksAreEmitted()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", "/tmp/tmux-1000/default,42,0"),
			("TERM", "tmux-256color"),
			("WT_SESSION", "test-session"),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().NotContain("]133;");
		harness.RawOutput.Should().NotContain("]633;");
	}

	[TestMethod]
	[Description("Auto mode honors a hosted session that advertises ShellIntegrationMarks through its terminal identity, without any local environment hint.")]
	public async Task When_ModeAuto_AndHostedSessionAdvertisesShellIntegrationMarks_Then_MarksAreEmitted()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		ReplSessionIO.TerminalIdentity = "Windows Terminal";
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().Contain("]133;A");
	}

	[TestMethod]
	[Description("A hosted session reporting a VS Code identity selects the OSC 633 backend even without TERM_PROGRAM in the host process environment.")]
	public async Task When_HostedSessionReportsVsCodeIdentity_Then_Osc633BackendIsSelected()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		ReplSessionIO.TerminalIdentity = "vscode";
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().Contain("]633;A");
		harness.RawOutput.Should().NotContain("]133;");
	}

	[TestMethod]
	[Description("Protocol passthrough (raw bytes piped to stdout) suppresses every mark regardless of mode: OSC bytes must never corrupt protocol streams.")]
	public async Task When_ProtocolPassthroughIsActive_Then_NoMarksAreEmitted()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		using var passthrough = ReplSessionIO.PushProtocolPassthrough();
		var emitter = CreateEmitter(ShellIntegrationMode.Always);

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().NotContain("]133;");
		harness.RawOutput.Should().NotContain("]633;");
	}

	[TestMethod]
	[Description("Disabled ANSI output suppresses every mark regardless of mode: escape bytes must never reach non-ANSI writers.")]
	public async Task When_AnsiIsDisabled_Then_NoMarksAreEmitted()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Never);
		var emitter = ShellIntegrationMarkEmitter.Create(
			new TerminalIntegrationOptions { ShellIntegration = ShellIntegrationMode.Always },
			new OutputOptions { AnsiMode = AnsiMode.Never });

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().NotContain("]133;");
		harness.RawOutput.Should().NotContain("]633;");
	}

	[TestMethod]
	[Description("A null options instance (UseTerminalIntegration never called) keeps the emitter fully disabled: the feature is opt-in.")]
	public async Task When_OptionsAreNull_Then_NoMarksAreEmitted()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", "test-session"),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = ShellIntegrationMarkEmitter.Create(
			options: null,
			new OutputOptions { AnsiMode = AnsiMode.Always });

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().NotContain("]133;");
		harness.RawOutput.Should().NotContain("]633;");
	}

	[TestMethod]
	[Description("The 633;E payload escapes backslashes, semicolons, and control characters per the VS Code shell-integration spec so the reported command line round-trips exactly.")]
	public void When_CommandLineContainsBackslashSemicolonAndControlChars_Then_Osc633PayloadIsEscaped()
	{
		ShellIntegrationMarkEmitter.EscapeCommandLine(@"a\b;c" + "\n")
			.Should().Be(@"a\\b\x3bc\x0a");
		ShellIntegrationMarkEmitter.EscapeCommandLine("plain text stays intact")
			.Should().Be("plain text stays intact");
		ShellIntegrationMarkEmitter.EscapeCommandLine("tab\there")
			.Should().Be(@"tab\x09here");
	}

	[TestMethod]
	[Description("An aborted or empty command reports D without an exit-code parameter, matching the FinalTerm 'command aborted' form.")]
	public async Task When_CommandEndsWithoutExitCode_Then_DIsEmittedWithoutParameter()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Always);

		await emitter.WritePromptStartAsync();
		await emitter.WriteInputStartAsync();
		await emitter.WriteCommandEndAsync(exitCode: null);

		harness.RawOutput.Should().Contain("]133;D");
		harness.RawOutput.Should().NotContain("]133;D;");
	}

	[TestMethod]
	[Description("The phase state machine makes a second command-end call a no-op so a command can never report two D marks.")]
	public async Task When_CommandEndIsCalledTwice_Then_OnlyOneDIsEmitted()
	{
		using var env = new EnvironmentVariableScope(NeutralTerminalEnvironment);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Always);

		await RunFullLifecycleAsync(emitter);
		await emitter.WriteCommandEndAsync(exitCode: 1);

		CountOccurrences(harness.RawOutput, "]133;D").Should().Be(1);
	}

	[TestMethod]
	[Description("The generic OSC 133 backend has no command-line mark, so WriteCommandLineAsync is a silent no-op outside VS Code.")]
	public async Task When_GenericBackendIsActive_Then_CommandLineMarkIsSuppressed()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", "test-session"),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Always);

		await emitter.WritePromptStartAsync();
		await emitter.WriteInputStartAsync();
		await emitter.WriteCommandLineAsync("ping");
		await emitter.WriteOutputStartAsync();
		await emitter.WriteCommandEndAsync(exitCode: 0);

		harness.RawOutput.Should().NotContain(";E;");
	}

	private static ShellIntegrationMarkEmitter CreateEmitter(ShellIntegrationMode mode) =>
		ShellIntegrationMarkEmitter.Create(
			new TerminalIntegrationOptions { ShellIntegration = mode },
			new OutputOptions { AnsiMode = AnsiMode.Always });

	private static async Task RunFullLifecycleAsync(ShellIntegrationMarkEmitter emitter)
	{
		await emitter.WritePromptStartAsync().ConfigureAwait(false);
		await emitter.WriteInputStartAsync().ConfigureAwait(false);
		await emitter.WriteOutputStartAsync().ConfigureAwait(false);
		await emitter.WriteCommandEndAsync(exitCode: 0).ConfigureAwait(false);
	}

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
