using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ShellIntegrationMarkEmitter
{

	[TestMethod]
	[Description("Always mode emits the full OSC 133 lifecycle in order: prompt start (A), input start (B), output start (C), command end with exit code (D).")]
	public async Task When_ModeAlways_AndAnsiSession_Then_LifecycleMarksAreEmittedInOrder()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
	[Description("Marks use the exact OSC framing — ESC introducer and BEL terminator — so a dropped ESC or a wrong terminator (ST vs BEL) is caught, not just the ]133;X substring.")]
	public async Task When_MarksAreEmitted_Then_FullOscFramingIsExact()
	{
		var esc = ((char)0x1b).ToString();
		var bel = ((char)0x07).ToString();
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
		raw.Should().Contain(esc + "]133;A" + bel);
		raw.Should().Contain(esc + "]133;B" + bel);
		raw.Should().Contain(esc + "]133;C" + bel);
		raw.Should().Contain(esc + "]133;D;0" + bel);
	}

	[TestMethod]
	[Description("The VS Code backend uses the same exact ESC/BEL framing on the 633 dialect, including the command-line report and the exit-code-less command end.")]
	public async Task When_VsCodeBackend_Then_Full633FramingIsExact()
	{
		var esc = ((char)0x1b).ToString();
		var bel = ((char)0x07).ToString();
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		ReplSessionIO.TerminalIdentity = "vscode";
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);

		await emitter.WritePromptStartAsync();
		await emitter.WriteInputStartAsync();
		await emitter.WriteCommandLineAsync("ping");
		await emitter.WriteOutputStartAsync();
		await emitter.WriteCommandEndAsync(exitCode: null);

		var raw = harness.RawOutput;
		raw.Should().Contain(esc + "]633;A" + bel);
		raw.Should().Contain(esc + "]633;B" + bel);
		raw.Should().Contain(esc + "]633;E;ping" + bel);
		raw.Should().Contain(esc + "]633;C" + bel);
		raw.Should().Contain(esc + "]633;D" + bel);
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
	[Description("Auto mode ignores the server process environment while a hosted session is active: a client that never advertised marks gets none even when the server runs inside Windows Terminal or VS Code.")]
	public async Task When_ModeAuto_AndHostedSessionLacksCapability_Then_ServerEnvironmentDoesNotEnableMarks()
	{
		using var env = new EnvironmentVariableScope(
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", "test-session"),
			("ConEmuANSI", null),
			("TERM_PROGRAM", "vscode"));
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().NotContain("]133;");
		harness.RawOutput.Should().NotContain("]633;");
	}

	[TestMethod]
	[Description("Backend selection ignores the server process environment for hosted sessions: a client advertising generic marks capability gets OSC 133 even when the server runs inside VS Code.")]
	public async Task When_HostedSessionAdvertisesMarks_AndServerRunsInsideVsCode_Then_GenericBackendIsUsed()
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
		ReplSessionIO.TerminalIdentity = "Windows Terminal";
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().Contain("]133;A");
		harness.RawOutput.Should().NotContain("]633;");
	}

	[TestMethod]
	[Description("A session reporting a VS Code identity selects the OSC 633 backend and reports the command line with 633;E between input start and output start.")]
	public async Task When_ModeAuto_AndVsCodeDetected_Then_Osc633MarksAreEmittedIncludingCommandLine()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		ReplSessionIO.TerminalIdentity = "vscode";
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
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
	[Description("The 633;E payload escapes backslashes, semicolons, spaces, and control characters per the VS Code shell-integration contract (0x20 and below) so multi-word command lines round-trip exactly.")]
	public void When_CommandLineContainsBackslashSemicolonAndControlChars_Then_Osc633PayloadIsEscaped()
	{
		ShellIntegrationMarkEmitter.EscapeCommandLine(@"a\b;c" + "\n")
			.Should().Be(@"a\\b\x3bc\x0a");
		ShellIntegrationMarkEmitter.EscapeCommandLine("git status")
			.Should().Be(@"git\x20status");
		ShellIntegrationMarkEmitter.EscapeCommandLine("single-word-stays-intact")
			.Should().Be("single-word-stays-intact");
		ShellIntegrationMarkEmitter.EscapeCommandLine("tab\there")
			.Should().Be(@"tab\x09here");

		// DEL (0x7f) and C1 controls (0x80-0x9f) must be escaped too: an unescaped
		// ST (U+009C) / OSC (U+009D) / CSI (U+009B) in pasted text would break out of
		// the 633;E payload and forge terminal sequences on xterm.js/VTE.
		ShellIntegrationMarkEmitter.EscapeCommandLine("del" + (char)0x7f + "here")
			.Should().Be(@"del\x7fhere");
		ShellIntegrationMarkEmitter.EscapeCommandLine("st" + (char)0x9c + "osc" + (char)0x9d + "csi" + (char)0x9b)
			.Should().Be(@"st\x9cosc\x9dcsi\x9b");
		ShellIntegrationMarkEmitter.EscapeCommandLine("high" + (char)0xe9 + "accent-stays")
			.Should().Be("high" + (char)0xe9 + "accent-stays");
		// A char above 0x9f (e.g. é, U+00E9) that appears AFTER an escapable char must
		// still be preserved: the escape predicate is only reached in the builder path,
		// so it must have an upper bound at 0x9f, not escape everything >= 0x80.
		ShellIntegrationMarkEmitter.EscapeCommandLine("echo caf" + (char)0xe9)
			.Should().Be(@"echo\x20caf" + (char)0xe9);
	}

	[TestMethod]
	[Description("A hosted client advertising ANSI only through capability flags (no AnsiSupport override) still gets marks: the server console's redirection state must not suppress a capability the client explicitly advertised.")]
	public async Task When_HostedClientAdvertisesAnsiThroughCapabilities_Then_MarksAreEmitted()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null);
		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.ShellIntegrationMarks;
		var emitter = ShellIntegrationMarkEmitter.Create(
			new TerminalIntegrationOptions { ShellIntegration = ShellIntegrationMode.Auto },
			new OutputOptions());

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().Contain("]133;A");
		harness.RawOutput.Should().Contain("]133;D;0");
	}

	[TestMethod]
	[Description("NO_COLOR is the documented end-user escape hatch and must win over the hosted capability fallback: a client advertising ANSI through capability flags still gets no marks when the operator disabled ANSI on the process.")]
	public async Task When_NoColorIsSet_Then_HostedCapabilityFallbackDoesNotReenableMarks()
	{
		using var env = new EnvironmentVariableScope(
			("NO_COLOR", "1"),
			("TMUX", null),
			("TERM", null),
			("WT_SESSION", null),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null);
		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.ShellIntegrationMarks;
		var emitter = ShellIntegrationMarkEmitter.Create(
			new TerminalIntegrationOptions { ShellIntegration = ShellIntegrationMode.Auto },
			new OutputOptions());

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().NotContain("]133;");
		emitter.LastGate.Should().Be(ShellIntegrationGate.AnsiUnsupported);
	}

	[TestMethod]
	[Description("TERM=dumb is the other documented environment opt-out and must also win over the hosted capability fallback, matching how styled output honors it.")]
	public async Task When_TermIsDumb_Then_HostedCapabilityFallbackDoesNotReenableMarks()
	{
		using var env = new EnvironmentVariableScope(
			("NO_COLOR", null),
			("TMUX", null),
			("TERM", "dumb"),
			("WT_SESSION", null),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null);
		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.ShellIntegrationMarks;
		var emitter = ShellIntegrationMarkEmitter.Create(
			new TerminalIntegrationOptions { ShellIntegration = ShellIntegrationMode.Auto },
			new OutputOptions());

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().NotContain("]133;");
		emitter.LastGate.Should().Be(ShellIntegrationGate.AnsiUnsupported);
	}

	[TestMethod]
	[Description("The session opener (lone D) only fixes a process-start anchor: when the FIRST enabled prompt used the generic backend and the client re-identifies as VS Code mid-session, no opener may fire between commands — a stray aborted D in a live stream can spawn a phantom command in VS Code's navigation state.")]
	public async Task When_BackendFlipsToVsCodeMidSession_Then_NoOpenerIsEmittedBetweenCommands()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		ReplSessionIO.TerminalIdentity = "Windows Terminal";
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);
		await RunFullLifecycleAsync(emitter);
		harness.RawOutput.Should().Contain("]133;A", because: "sanity: cycle 1 ran on the generic backend");

		ReplSessionIO.TerminalIdentity = "vscode";
		await RunFullLifecycleAsync(emitter);

		var raw = harness.RawOutput;
		var first633CommandEnd = raw.IndexOf("]633;D", StringComparison.Ordinal);
		var first633PromptStart = raw.IndexOf("]633;A", StringComparison.Ordinal);
		first633PromptStart.Should().BeGreaterThanOrEqualTo(0, because: "cycle 2 runs on the VS Code backend");
		first633CommandEnd.Should().BeGreaterThan(
			first633PromptStart,
			because: "the first 633 D must be cycle 2's own command end, not a mid-session opener");
	}

	[TestMethod]
	[Description("CLICOLOR_FORCE=1 overrides TERM=dumb in the hosted capability fallback, matching styled output's precedence: with an ANSI-incapable server console, a capable hosted client still gets marks when the operator explicitly forced color.")]
	public async Task When_ClicolorForceOverridesDumbTerm_Then_HostedCapabilityFallbackEmitsMarks()
	{
		using var env = new EnvironmentVariableScope(
			("NO_COLOR", null),
			("CLICOLOR_FORCE", "1"),
			("TMUX", null),
			("TERM", "dumb"),
			("WT_SESSION", null),
			("ConEmuANSI", null),
			("TERM_PROGRAM", null));
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null);
		ReplSessionIO.TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.ShellIntegrationMarks;
		var outputOptions = new OutputOptions();
		// An ANSI-incapable server console: forces IsAnsiEnabled onto its host-detection
		// branch so the capability fallback (the code under test) is what decides.
		outputOptions.SetHostAnsiSupportResolver(static () => false);
		var emitter = ShellIntegrationMarkEmitter.Create(
			new TerminalIntegrationOptions { ShellIntegration = ShellIntegrationMode.Auto },
			outputOptions);

		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().Contain("]133;A");
		harness.RawOutput.Should().Contain("]133;D;0");
	}

	[TestMethod]
	[Description("The IsWindows property bytes are pinned exactly: the emission branch only runs on a local Windows console, which the hosted test harness cannot exercise, so a typo in the literal would otherwise ship undetected.")]
	public void When_CheckingWindowsPtyPropertyBytes_Then_SequenceIsExact()
	{
		ShellIntegrationMarkEmitter.WindowsPtyProperty.Should().Be("\x1b]633;P;IsWindows=True\x07");
	}

	[TestMethod]
	[Description("A hosted client advertising ShellIntegrationMarks after the session started (Telnet TTYPE, control messages) gets marks from the next prompt cycle: enablement is re-evaluated per cycle, not frozen at session start.")]
	public async Task When_HostedSessionAdvertisesMarksMidSession_Then_MarksAppearOnNextPromptCycle()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);

		await RunFullLifecycleAsync(emitter);
		harness.RawOutput.Should().NotContain("]133;", because: "the client has not advertised marks yet");
		ReplSessionIO.TerminalIdentity = "Windows Terminal";
		await RunFullLifecycleAsync(emitter);

		harness.RawOutput.Should().Contain("]133;A");
		harness.RawOutput.Should().Contain("]133;D;0");
	}

	[TestMethod]
	[Description("Auto mode honors a mid-session identity downgrade: capability bits earned only by a previous identity inference are dropped when the client re-identifies as a markless terminal, so marks stop on the next prompt cycle instead of flowing forever.")]
	public async Task When_HostedClientDowngradesToDumbMidSession_Then_MarksStopOnNextPromptCycle()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);
		ReplSessionIO.TerminalIdentity = "Windows Terminal";
		await RunFullLifecycleAsync(emitter);
		harness.RawOutput.Should().Contain("]133;A", because: "sanity: marks flow while the client identifies as Windows Terminal");

		ReplSessionIO.TerminalIdentity = "dumb";
		var markCountBeforeSecondCycle = TerminalMarks.Count(harness.RawOutput, "]133;");
		await RunFullLifecycleAsync(emitter);

		TerminalMarks.Count(harness.RawOutput, "]133;")
			.Should().Be(markCountBeforeSecondCycle, because: "the latest identity no longer advertises shell-integration marks");
	}

	[TestMethod]
	[Description("An aborted or empty command reports D without an exit-code parameter, matching the FinalTerm 'command aborted' form.")]
	public async Task When_CommandEndsWithoutExitCode_Then_DIsEmittedWithoutParameter()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Always);

		await RunFullLifecycleAsync(emitter);
		await emitter.WriteCommandEndAsync(exitCode: 1);

		TerminalMarks.Count(harness.RawOutput, "]133;D").Should().Be(1);
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

	[TestMethod]
	[Description("Each prompt cycle records which gate decided the enablement, in the documented order, so a wrong on/off decision is triaged exactly instead of by symptom guessing. This pins the hosted Auto path: not advertised → advertised.")]
	public async Task When_HostedAutoResolves_Then_DecidingGateIsRecorded()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Auto);

		await emitter.WritePromptStartAsync();
		var beforeAdvertising = emitter.LastGate;
		ReplSessionIO.TerminalIdentity = "Windows Terminal";
		await emitter.WritePromptStartAsync();
		var afterAdvertising = emitter.LastGate;

		beforeAdvertising.Should().Be(ShellIntegrationGate.SessionNotAdvertising);
		afterAdvertising.Should().Be(ShellIntegrationGate.Enabled);
	}

	[TestMethod]
	[Description("The protocol-passthrough gate is recorded as the deciding reason when a passthrough scope is active, ahead of any mode or capability consideration.")]
	public async Task When_ProtocolPassthroughIsActive_Then_GateRecordsIt()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var emitter = CreateEmitter(ShellIntegrationMode.Always);

		using var passthrough = ReplSessionIO.PushProtocolPassthrough();
		await emitter.WritePromptStartAsync();

		emitter.LastGate.Should().Be(ShellIntegrationGate.ProtocolPassthrough);
	}

	[TestMethod]
	[Description("An app that never called UseTerminalIntegration records the not-configured gate, and Never mode records the mode gate — the two 'off by design' reasons stay distinguishable.")]
	public async Task When_IntegrationIsOffByDesign_Then_GateDistinguishesWhy()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(
			output: harness.Writer,
			input: TextReader.Null,
			ansiMode: AnsiMode.Always);
		var notConfigured = ShellIntegrationMarkEmitter.Create(options: null, new OutputOptions { AnsiMode = AnsiMode.Always });
		var neverMode = CreateEmitter(ShellIntegrationMode.Never);

		await notConfigured.WritePromptStartAsync();
		await neverMode.WritePromptStartAsync();

		notConfigured.LastGate.Should().Be(ShellIntegrationGate.NotConfigured);
		neverMode.LastGate.Should().Be(ShellIntegrationGate.ModeNever);
	}

	[TestMethod]
	[Description("IsWindows=True is reported only for a local console on Windows, where ConPTY sits between the app and the terminal: a hosted session keeps VS Code's position-trusting default, and non-Windows hosts have no ConPTY to compensate for.")]
	public void When_CheckingConPtyReporting_Then_OnlyLocalWindowsConsoleQualifies()
	{
		bool insideSession;
		using (ReplSessionIO.SetSession(new StringWriter(), TextReader.Null))
		{
			insideSession = ShellIntegrationMarkEmitter.ShouldReportWindowsConPty();
		}

		var outsideSession = ShellIntegrationMarkEmitter.ShouldReportWindowsConPty();

		insideSession.Should().BeFalse(because: "hosted transports deliver bytes verbatim, without ConPTY in the path");
		outsideSession.Should().Be(OperatingSystem.IsWindows(), because: "a local console goes through ConPTY exactly when the host OS is Windows");
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
}
