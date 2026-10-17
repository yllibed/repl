using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
[DoNotParallelize]
public sealed class Given_InteractiveSession_ShellIntegrationMarks
{
	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	[Description("Preprocessing application failures, including unrelated cancellation exceptions, must open output before the diagnostic and report failure rather than interruption.")]
	public void When_InputCallbackFails_Then_TheDiagnosticHasACompleteShellLifecycle(bool cancelled)
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.UseArgumentPreprocessor((input, _) =>
		{
			if (input.Mode == InputProcessingMode.Execution && input.Arguments is ["boom"])
			{
				throw cancelled ? new OperationCanceledException("private timeout") : new InvalidOperationException("input callback failed");
			}
			return ValueTask.FromResult(ArgumentPreprocessingResult.Continue());
		});
		var harness = new TerminalHarness(cols: 80, rows: 12);
		var raw = RunInteractiveSession(harness, sut, "boom\rexit\r");
		var start = raw.IndexOf("]133;C", StringComparison.Ordinal);
		var diagnostic = raw.IndexOf(cancelled ? "private timeout" : "input callback failed", StringComparison.Ordinal);
		var end = raw.IndexOf("]133;D;1", StringComparison.Ordinal);
		raw.Should().NotContain("Cancelled.");
		start.Should().BeGreaterThanOrEqualTo(0);
		diagnostic.Should().BeGreaterThan(start);
		end.Should().BeGreaterThan(diagnostic);
		var commandRegion = raw[..(end + 1)];
		commandRegion.Split("]133;C", StringSplitOptions.None).Length.Should().Be(2);
	}

	[TestMethod]
	[Description("A successful interactive command is wrapped by the full lifecycle: prompt start, input start, output start, command output, and command end with exit code 0, then the next prompt starts a new cycle.")]
	public void When_CommandSucceeds_Then_PromptInputOutputAndEndMarksWrapTheCommand()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("boom", () => Results.Error("boom-failed", "nope"));
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "boom\rexit\r");

		raw.Should().Contain("]133;D;1");
	}

	[TestMethod]
	[Description("An unknown command resolves to a route-resolution failure and reports the usage exit code (2) in the command-end mark.")]
	public void When_UnknownCommandIsEntered_Then_CommandEndReportsUsageExitCode()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "zorglub\rexit\r");

		raw.Should().Contain("]133;D;2");
	}

	[TestMethod]
	[Description("An ambiguous command prefix renders its error inside the normal lifecycle and reports the usage exit code (2) in the command-end mark, like any other failed input.")]
	public void When_AmbiguousPrefixIsCommitted_Then_CommandEndReportsUsageExitCode()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("gabu", () => "bu");
		sut.Map("gazo", () => "zo");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "ga\rexit\r");

		raw.Should().Contain("Ambiguous command prefix");
		raw.Should().Contain("]133;D;2");
	}

	[TestMethod]
	[Description("Regression guard: verifies the command-end mark follows the configured exit-code table, so an application that remaps usage errors sees its own code in the terminal decoration.")]
	public void When_UsageErrorIsRemapped_Then_CommandEndMarkFollowsTable()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Options(options => options.ExitCodes.UsageError = 64);
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "zorglub\rexit\r");

		raw.Should().Contain("]133;D;64");
	}

	[TestMethod]
	[Description("Regression guard: verifies the command-end mark goes through ExitCodes.Resolver, so an application-wide exit-code convention is visible in the terminal decoration too.")]
	public void When_ResolverIsConfigured_Then_CommandEndMarkUsesItsReturnValue()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Options(options => options.ExitCodes.Resolver = outcome =>
			outcome.Kind == ReplExecutionOutcomeKind.HandlerError ? 70 : outcome.ExitCode);
		sut.Map("fail", () => Results.Error("boom", "failed"));
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "fail\rexit\r");

		raw.Should().Contain("]133;D;70");
	}

	[TestMethod]
	[Description("Regression guard: verifies a configured ExitCodes.Cancelled replaces the conventional 130 in the command-end mark. The handler raises OperationCanceledException itself; the interactive loop deliberately treats that as an abort, unlike the one-shot path.")]
	public void When_HandlerRaisesCancellationAndCancelledIsConfigured_Then_MarkUsesConfiguredCode()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Options(options => options.ExitCodes.Cancelled = 7);
		sut.Map("boom", string () => throw new OperationCanceledException());
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "boom\rexit\r");

		raw.Should().Contain("Cancelled.");
		raw.Should().Contain("]133;D;7");
	}

	[TestMethod]
	[Description("Regression guard: verifies interactive help is classified Help, not a generic success, so an application that maps ExitCodes.Help separately sees its own code in the command-end mark.")]
	public void When_HelpIsRemapped_Then_InteractiveHelpCommandEndUsesTheHelpCode()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Options(options => options.ExitCodes.Help = 3);
		sut.Map("ping", () => "pong").WithDescription("Answers with pong.");
		var harness = new TerminalHarness(cols: 80, rows: 24);

		var raw = RunInteractiveSession(harness, sut, "help\rexit\r");

		// help reports the Help code; the exit ambient that follows still reports Success.
		raw.Should().Contain("]133;D;3");
		TerminalMarks.Count(raw, "]133;D;0").Should().Be(1);
	}

	[TestMethod]
	[Description("Regression guard: verifies a malformed global option is refused in the interactive loop as it is in a one-shot run, so the command-end mark carries the usage code instead of the help or success code. The loop parses globals per command and used to check them on no path at all.")]
	public void When_InteractiveInputHasAMalformedGlobal_Then_MarkReportsUsageError()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("hello", () => "world");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "hello --help --result:page-size\rexit\r");

		raw.Should().Contain("]133;D;2");
		raw.Should().NotContain("]133;D;0\u001b]133;A", "the malformed global must not report a successful command");
	}

	[TestMethod]
	[Description("Regression guard: verifies a help invocation that cannot render is a refusal first: the usage code wins over the Help classification the ambient entry would otherwise report.")]
	public void When_HelpIsRemappedAndHelpFailsToRender_Then_UsageCodeStillWins()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Options(options => options.ExitCodes.Help = 3);
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "help --output:bogus\rexit\r");

		raw.Should().Contain("]133;D;2");
		raw.Should().NotContain("]133;D;3");
	}

	[TestMethod]
	[Description("Regression guard: verifies the resolver sees Scope.ShellIntegrationMark for a command-end code and runs once per committed command, so a hook with side effects can tell a terminal decoration from a process exit.")]
	public void When_MarksAreEnabled_Then_ResolverRunsPerCommandWithMarkScope()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var scopes = new List<ReplExitCodeScope>();
		var sut = CreateMarkedApp();
		sut.Options(options => options.ExitCodes.Resolver = outcome =>
		{
			scopes.Add(outcome.Scope);
			return outcome.ExitCode;
		});
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		_ = RunInteractiveSession(harness, sut, "ping\rexit\r");

		// ping and exit each carry a mark, then the session's own process exit code.
		scopes.Should().Equal(
			ReplExitCodeScope.ShellIntegrationMark,
			ReplExitCodeScope.ShellIntegrationMark,
			ReplExitCodeScope.Process);
	}

	[TestMethod]
	[Description("Regression guard: with shell integration off the loop emits no command-end codes, so the resolver runs once for the process exit instead of once per command.")]
	public void When_MarksAreDisabled_Then_ResolverRunsOnlyForTheProcessExit()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var scopes = new List<ReplExitCodeScope>();
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Options(options => options.ExitCodes.Resolver = outcome =>
		{
			scopes.Add(outcome.Scope);
			return outcome.ExitCode;
		});
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		_ = RunInteractiveSession(harness, sut, "ping\rexit\r");

		scopes.Should().Equal(ReplExitCodeScope.Process);
	}

	[TestMethod]
	[Description("Regression guard: verifies the failed-dispatch command-end mark goes through the exit-code table like every other mark, instead of hard-coding 1.")]
	public void When_HandlerExceptionIsRemapped_Then_FailedDispatchCommandEndUsesTheConfiguredCode()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Options(options => options.ExitCodes.HandlerException = 70);
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "history --limit abc\r", swallowRunExceptions: true);

		raw.Should().Contain("]133;D;70");
	}

	[TestMethod]
	[Description("Regression guard: a resolver that throws on the command-end path must not replace the original dispatch exception, which is the signal that matters.")]
	public void When_ResolverThrowsOnAFailedDispatch_Then_TheOriginalExceptionSurfaces()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Options(options => options.ExitCodes.Resolver = _ => throw new FormatException("resolver boom"));
		sut.Map("ping", () => "pong");
		using var writer = new StringWriter();

		var captured = CaptureInteractiveRun(writer, sut, "history --limit abc\r");

		captured.Should().NotBeNull();
		captured!.Message.Should().Contain("--limit");
	}

	[TestMethod]
	[Description("Regression guard: verifies ambient commands such as help run inside the same command lifecycle: their output lands between output-start and a successful command-end mark.")]
	public void When_HelpAmbientCommandRuns_Then_MarksWrapHelpOutput()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "\rexit\r");

		var firstCommandEnd = raw.IndexOf("]133;D", StringComparison.Ordinal);
		var firstOutputStart = raw.IndexOf("]133;C", StringComparison.Ordinal);
		firstCommandEnd.Should().BeGreaterThanOrEqualTo(0);
		MarkPayloadAt(raw, firstCommandEnd).Should().NotContain("D;", because: "an aborted cycle reports D without an exit-code parameter");
		firstOutputStart.Should().BeGreaterThan(firstCommandEnd, because: "the only output-start mark belongs to the later exit command");
	}

	[TestMethod]
	[Description("Escaping at an empty prompt abandons the cycle: command-end is reported without an exit code, matching the FinalTerm aborted-command form.")]
	public void When_EscapeAbandonsThePrompt_Then_CommandEndsWithoutExitCode()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "\u001bexit\r");

		var firstCommandEnd = raw.IndexOf("]133;D", StringComparison.Ordinal);
		firstCommandEnd.Should().BeGreaterThanOrEqualTo(0);
		MarkPayloadAt(raw, firstCommandEnd).Should().NotContain("D;", because: "an aborted cycle reports D without an exit-code parameter");
	}

	[TestMethod]
	[Description("The exit ambient command closes its own cycle: the final command-end mark reports exit code 0 and no mark follows it.")]
	public void When_ExitCommandRuns_Then_FinalCommandEndIsEmittedBeforeSessionEnds()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "exit\r");

		var commandEnd = raw.IndexOf("]133;D;0", StringComparison.Ordinal);
		commandEnd.Should().BeGreaterThanOrEqualTo(0);
		raw.IndexOf("]133;", commandEnd + 1, StringComparison.Ordinal).Should().Be(-1);
	}

	[TestMethod]
	[Description("Regression guard: verifies an OperationCanceledException escaping an interactive command keeps the Cancelled. message and reports 130 (128+SIGINT), the shell convention terminals read as interrupted. The handler raises it itself: interactively that is an abort by design, where a one-shot run would render it as a handler failure. Real Ctrl+C reaches the same arm.")]
	public void When_HandlerThrowsOperationCanceled_Then_CancelledLineIsPrintedAndExitCodeIs130()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
	[Description("The VS Code backend declares the prompt text via OSC 633;P;Prompt before the first input-start mark, so VS Code's marker-adjusting heuristics can recognize the custom prompt line (ConPTY rewrites make parse-time positions unreliable on Windows).")]
	public void When_VsCodeBackendRuns_Then_PromptPropertyIsReportedBeforeInputStart()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp(ShellIntegrationMode.Auto);
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "ping\rexit\r", terminalIdentity: "vscode");

		raw.Should().Contain("]633;P;Prompt=>", because: "the interactive prompt ('>') must be declared to the terminal");
		raw.IndexOf("]633;P;Prompt=", StringComparison.Ordinal).Should().BeLessThan(
			raw.IndexOf("]633;B", StringComparison.Ordinal),
			because: "properties must precede the first input-start so the heuristics already apply to the first command");
	}

	[TestMethod]
	[Description("The VS Code backend opens the session with a lone command-end (no exit code) before the first prompt-start: when the app was launched from an integrated shell, that shell's command is still open and VS Code would anchor our first prompt at its stale end position (the banner). The opener closes it at the true cursor position.")]
	public void When_VsCodeBackendRuns_Then_SessionOpensWithALoneCommandEndBeforeTheFirstPromptStart()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp(ShellIntegrationMode.Auto);
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "ping\rexit\r", terminalIdentity: "vscode");

		var firstCommandEnd = raw.IndexOf("]633;D", StringComparison.Ordinal);
		var firstPromptStart = raw.IndexOf("]633;A", StringComparison.Ordinal);
		firstCommandEnd.Should().BeGreaterThanOrEqualTo(0);
		firstCommandEnd.Should().BeLessThan(firstPromptStart, because: "the opener must close the outer shell's command before our first prompt anchors");
		MarkPayloadAt(raw, firstCommandEnd).Should().NotContain("D;", because: "the opener uses the aborted form — we cannot know the outer command's exit code");
	}

	[TestMethod]
	[Description("The generic OSC 133 backend does not emit the session opener: the stale-anchor behavior it compensates for is specific to VS Code's command detection, and a leading D would break the mark-count expectations of FinalTerm terminals.")]
	public void When_Osc133BackendRuns_Then_NoCommandEndPrecedesTheFirstPromptStart()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp(ShellIntegrationMode.Auto);
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "ping\rexit\r", terminalIdentity: "Windows Terminal");

		var firstCommandEnd = raw.IndexOf("]133;D", StringComparison.Ordinal);
		var firstPromptStart = raw.IndexOf("]133;A", StringComparison.Ordinal);
		firstPromptStart.Should().BeGreaterThanOrEqualTo(0);
		firstCommandEnd.Should().BeGreaterThan(firstPromptStart, because: "the first D must be the first command's own end, not a session opener");
	}

	[TestMethod]
	[Description("Hosted sessions never report 633;P;IsWindows: the transport delivers bytes verbatim to the remote terminal, so VS Code's position-trusting default is correct there — ConPTY compensation is a local-console concern.")]
	public void When_HostedVsCodeSessionRuns_Then_IsWindowsPropertyIsNotReported()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp(ShellIntegrationMode.Auto);
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "ping\rexit\r", terminalIdentity: "vscode");

		raw.Should().NotContain("]633;P;IsWindows");
	}

	[TestMethod]
	[Description("The generic OSC 133 backend reports no P properties: the property sequence is a VS Code 633-dialect concept and would be garbage on FinalTerm-only terminals.")]
	public void When_Osc133BackendRuns_Then_NoPropertySequenceIsReported()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp(ShellIntegrationMode.Auto);
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "ping\rexit\r", terminalIdentity: "Windows Terminal");

		raw.Should().Contain("]133;A", because: "sanity: the generic backend is active");
		raw.Should().NotContain(";P;Prompt");
		raw.Should().NotContain(";P;IsWindows");
	}

	[TestMethod]
	[Description("Handlers can read the shell-integration autodetection outcome through IReplSessionInfo.ShellIntegrationStatus: the active protocol when marks are on ('OSC 633 (VS Code)'), so a debug command can show which dialect the terminal negotiated.")]
	public void When_HandlerReadsSessionInfo_Then_ShellIntegrationStatusExposesTheDetection()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp(ShellIntegrationMode.Auto);
		string? observed = null;
		sut.Map("probe", (IReplSessionInfo session) =>
		{
			observed = session.ShellIntegrationStatus;
			return "ok";
		});
		var harness = new TerminalHarness(cols: 80, rows: 12);

		_ = RunInteractiveSession(harness, sut, "probe\rexit\r", terminalIdentity: "vscode");

		observed.Should().Be("OSC 633 (VS Code)");
	}

	[TestMethod]
	[Description("When marks are off, ShellIntegrationStatus names the deciding gate, so a sample or debug command can explain why nothing is emitted instead of guessing from symptoms.")]
	public void When_IntegrationNotConfigured_Then_ShellIntegrationStatusNamesTheGate()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Options(options => options.Output.AnsiMode = AnsiMode.Always);
		string? observed = null;
		sut.Map("probe", (IReplSessionInfo session) =>
		{
			observed = session.ShellIntegrationStatus;
			return "ok";
		});
		var harness = new TerminalHarness(cols: 80, rows: 12);

		_ = RunInteractiveSession(harness, sut, "probe\rexit\r");

		observed.Should().Be("off (NotConfigured)");
	}

	[TestMethod]
	[Description("A failed completion ambient command (complete without --target) reports the usage exit code (2) in the command-end mark instead of decorating the failure as success.")]
	public void When_CompleteAmbientCommandFails_Then_CommandEndReportsUsageExitCode()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "complete\rexit\r");

		raw.Should().Contain("Error: complete requires --target");
		raw.Should().Contain("]133;D;2");
	}

	[TestMethod]
	[Description("An ambient help invocation that fails to render (unknown output format) reports the usage exit code (2) in the command-end mark, matching the non-ambient --help path.")]
	public void When_HelpAmbientCommandFailsToRender_Then_CommandEndReportsUsageExitCode()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "help --output:bogus\rexit\r");

		raw.Should().Contain("]133;D;2");
	}

	[TestMethod]
	[Description("A passthrough route invoked with a leading global option (--json) is still classified as passthrough by the single committed-input resolution, so no output marks wrap its payload.")]
	public void When_PassthroughCommandHasGlobalOption_Then_NoOutputMarksWrapThePayload()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("serve", (IReplIoContext _) => "protocol-payload").AsProtocolPassthrough();
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "serve --json\rexit\r");

		TerminalMarks.Count(raw, "]133;C").Should().Be(1, because: "only the exit cycle may open an output region");
		TerminalMarks.Count(raw, "]133;D").Should().Be(1, because: "no command-end mark may trail the protocol payload");
	}

	[TestMethod]
	[Description("A protocol-passthrough command run interactively gets no output-start or command-end marks: OSC bytes must never precede or trail a protocol payload on the same stream.")]
	public void When_ProtocolPassthroughCommandRunsInteractively_Then_NoOutputMarksWrapTheProtocolStream()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("serve", (IReplIoContext _) => "protocol-payload").AsProtocolPassthrough();
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "serve\rexit\r");

		raw.Should().Contain("protocol-payload");
		TerminalMarks.Count(raw, "]133;C").Should().Be(1, because: "only the exit cycle may open an output region");
		TerminalMarks.Count(raw, "]133;D").Should().Be(1, because: "no command-end mark may trail the protocol payload");
		raw.IndexOf("]133;C", StringComparison.Ordinal)
			.Should().BeGreaterThan(
				raw.IndexOf("protocol-payload", StringComparison.Ordinal),
				because: "the only output-start mark belongs to the later exit command");
	}

	[TestMethod]
	[Description("Once a protocol-passthrough invocation dispatches, no command-end mark may be emitted even on failure: an error exit cannot prove the payload never started, so the cycle is abandoned silently.")]
	public void When_PassthroughCommandFailsValidation_Then_CycleIsAbandonedWithoutMarks()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("serve", (IReplIoContext _) => "protocol-payload").AsProtocolPassthrough();
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "serve --bogus 42\rexit\r");

		raw.Should().NotContain("protocol-payload");
		TerminalMarks.Count(raw, "]133;C").Should().Be(1, because: "only the exit cycle may open an output region");
		TerminalMarks.Count(raw, "]133;D").Should().Be(1, because: "only the exit cycle may report a command end");
	}

	[TestMethod]
	[Description("A passthrough handler that emits bytes and then returns a nonzero exit gets no trailing command-end mark: OSC bytes must never follow a protocol payload, whatever the exit code.")]
	public void When_PassthroughHandlerExitsNonZero_Then_NoMarkTrailsThePayload()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("serve", (IReplIoContext _) => Results.Exit(7)).AsProtocolPassthrough();
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "serve\rexit\r");

		raw.Should().NotContain("]133;D;7");
		TerminalMarks.Count(raw, "]133;D").Should().Be(1, because: "only the exit cycle may report a command end");
	}

	[TestMethod]
	[Description("A protocol-passthrough route dispatched from the interactive loop runs under the same passthrough contract as CLI one-shot execution: the handler observes an active protocol-passthrough scope, so the stdout/stderr/session isolation contract holds in both modes.")]
	public void When_PassthroughCommandRunsInteractively_Then_HandlerObservesPassthroughScope()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		bool? observedPassthrough = null;
		sut.Map(
				"serve",
				(IReplIoContext _) =>
				{
					observedPassthrough = ReplSessionIO.IsProtocolPassthrough;
					return "protocol-payload";
				})
			.AsProtocolPassthrough();
		var harness = new TerminalHarness(cols: 80, rows: 12);

		_ = RunInteractiveSession(harness, sut, "serve\rexit\r");

		observedPassthrough.Should().BeTrue(
			because: "interactive dispatch must honor the same protocol-passthrough contract as the CLI one-shot path");
	}

	[TestMethod]
	[Description("In a hosted interactive session, a protocol-passthrough route whose handler cannot run hosted (no IReplIoContext parameter) is rejected with the same error as the CLI one-shot path, instead of silently running without the isolation contract.")]
	public void When_HostedInteractivePassthroughLacksIoContext_Then_HostedGuardRejectsLikeCli()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		var handlerRan = false;
		sut.Map(
				"serve",
				() =>
				{
					handlerRan = true;
					return "protocol-payload";
				})
			.AsProtocolPassthrough();
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "serve\rexit\r");

		handlerRan.Should().BeFalse(because: "the hosted guard must reject before dispatch, matching CLI one-shot behavior");
		raw.Should().Contain("requires a handler parameter of type IReplIoContext");
	}

	[TestMethod]
	[Description("An ambient command that shares its token with a protocol-passthrough route is handled ambient-first and keeps the normal lifecycle: output-start and a command-end mark wrap its terminal output.")]
	public void When_AmbientCommandSharesTokenWithPassthroughRoute_Then_AmbientLifecycleApplies()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("history", () => "protocol-payload").AsProtocolPassthrough();
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "history\rexit\r");

		raw.Should().NotContain("protocol-payload", because: "the ambient history command wins over the route");
		TerminalMarks.Count(raw, "]133;C").Should().Be(2, because: "the ambient command's output is normal terminal output");
		TerminalMarks.Count(raw, "]133;D;0").Should().Be(2);
	}

	[TestMethod]
	[Description("A prefix-abbreviated protocol-passthrough route (ser -> serve) is classified as passthrough by the single committed-input resolution — prefix expansion and the route match share one graph snapshot — so no output marks wrap its payload.")]
	public void When_PrefixAbbreviatedPassthroughRuns_Then_NoOutputMarksWrapThePayload()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("serve", (IReplIoContext _) => "protocol-payload").AsProtocolPassthrough();
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "ser\rexit\r");

		raw.Should().Contain("protocol-payload");
		TerminalMarks.Count(raw, "]133;C").Should().Be(1, because: "only the exit cycle may open an output region");
		TerminalMarks.Count(raw, "]133;D").Should().Be(1, because: "no command-end mark may trail the abbreviated passthrough payload");
	}

	[TestMethod]
	[Description("Requesting --help on a protocol-passthrough route only renders help, so the normal lifecycle applies: the help cycle gets output-start and a successful command-end mark.")]
	public void When_PassthroughRouteRequestsHelp_Then_LifecycleMarksApplyNormally()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("serve", () => "protocol-payload").AsProtocolPassthrough();
		var harness = new TerminalHarness(cols: 80, rows: 24);

		var raw = RunInteractiveSession(harness, sut, "serve --help\rexit\r");

		TerminalMarks.Count(raw, "]133;C").Should().Be(2, because: "help rendering is normal terminal output, not a protocol payload");
		TerminalMarks.Count(raw, "]133;D;0").Should().Be(2);
	}

	[TestMethod]
	[Description("An interactive passthrough handler that cancels itself keeps the interactive cancellation semantics: a passthrough command carries no scope tokens whatever the mode, so the mode must come from the session, not from that argument.")]
	public void When_InteractivePassthroughHandlerCancelsItself_Then_CancellationIsReportedNotAHandlerFailure()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		// Takes IReplIoContext so the hosted-session passthrough guard lets it dispatch and the handler
		// actually runs; without it the run stops at that guard instead of reaching the cancellation.
		sut.Map("serve", string (IReplIoContext io) => throw new OperationCanceledException())
			.AsProtocolPassthrough();
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "serve\rexit\r");

		// The interactive loop owns cancellation: it prints "Cancelled." and keeps the session alive,
		// instead of the one-shot path rendering an execution_error and reporting a handler failure.
		raw.Should().Contain("Cancelled.");
		raw.Should().NotContain("execution_error");
	}

	[TestMethod]
	[Description("A dispatch that throws (history with a non-numeric --limit) still closes the lifecycle with a failed command-end mark so the terminal never keeps an unterminated command segment.")]
	public void When_AmbientCommandThrows_Then_CommandEndStillReportsFailure()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
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
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("work", async (IReplInteractionChannel channel) =>
		{
			await channel.WriteStatusAsync("working on it", CancellationToken.None).ConfigureAwait(false);
			return "done";
		});
		var harness = new TerminalHarness(cols: 80, rows: 12);

		var raw = RunInteractiveSession(harness, sut, "work\rexit\r");

		raw.Should().Contain("working on it");
		TerminalMarks.Count(raw, "]133;A").Should().Be(2, because: "only the work and exit prompt cycles may open a lifecycle");
	}

	[TestMethod]
	[Description("When the command-end mark write itself fails (torn-down transport), the original dispatch exception must surface, not the mark-write failure that only happened during cleanup.")]
	public void When_CommandEndMarkWriteFails_Then_OriginalExceptionSurfaces()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);
		// `history --limit abc` raises InvalidOperationException from the ambient handler,
		// which propagates to the cleanup catch; the writer then throws on the D-mark write.
		var writer = new MarkFailingWriter(harness.Writer, failOn: "]133;D");

		var thrown = CaptureInteractiveRun(writer, sut, "history --limit abc\r");

		writer.Threw.Should().BeTrue(because: "the fault injection must actually fire for this test to prove anything");
		thrown.Should().BeOfType<InvalidOperationException>(because: "the ambient handler's failure is the original exception");
		thrown!.Message.Should().Contain("--limit", because: "the exception must be the ambient --limit failure, not the mark-write IOException");
	}

	[TestMethod]
	[Description("If the line read throws after the prompt marks are written (A/B), the loop closes the open cycle with an aborted command-end mark (no exit code) before the exception propagates, so the terminal keeps no unterminated command segment.")]
	public void When_LineReadFailsAfterPromptMarks_Then_AbortedCommandEndIsEmitted()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);

		// An empty key queue makes ConsoleLineReader's first ReadKeyAsync throw, after
		// WritePromptStart (A) and WriteInputStart (B) have already run for that cycle.
		var raw = RunInteractiveSession(harness, sut, typedInput: string.Empty, swallowRunExceptions: true);

		raw.Should().Contain("]133;B", because: "the prompt cycle opened before the read failed");
		raw.Should().Contain("]133;D", because: "the failed read must still close the cycle");
		raw.Should().NotContain("]133;D;", because: "a read failure is an aborted cycle, not a failure exit code");
	}

	[TestMethod]
	[Description("CLI one-shot execution emits no marks even with UseTerminalIntegration configured, Always mode, and a fully capable terminal: Repl does not own the surrounding shell prompt there, and protocol streams (MCP stdio) must stay clean. Pins the guarantee that is otherwise only structural (the interactive loop owns the emitter).")]
	public void When_OneShotRunsWithIntegrationConfigured_Then_NoMarksAreEmitted()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var sut = CreateMarkedApp();
		sut.Map("ping", () => "pong");
		var harness = new TerminalHarness(cols: 80, rows: 12);
		using var session = ReplSessionIO.SetSession(harness.Writer, TextReader.Null);
		ReplSessionIO.AnsiSupport = true;
		ReplSessionIO.TerminalIdentity = "Windows Terminal";

		var exitCode = sut.Run(["ping"]);

		exitCode.Should().Be(0);
		harness.RawOutput.Should().Contain("pong");
		harness.RawOutput.Should().NotContain("]133;");
		harness.RawOutput.Should().NotContain("]633;");
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
		_ = RunInteractiveSessionCore(
			harness.Writer, (harness.Cols, harness.Rows), sut, typedInput, terminalIdentity, swallowRunExceptions);
		return harness.RawOutput;
	}

	private static InvalidOperationException? CaptureInteractiveRun(TextWriter writer, ReplApp sut, string typedInput) =>
		RunInteractiveSessionCore(
			writer, size: (80, 12), sut, typedInput, terminalIdentity: null, swallowRunExceptions: true);

	// Single session-scope setup shared by the raw-output and captured-exception entry
	// points, so the two cannot drift apart on session metadata.
	private static InvalidOperationException? RunInteractiveSessionCore(
		TextWriter writer,
		(int Cols, int Rows) size,
		ReplApp sut,
		string typedInput,
		string? terminalIdentity,
		bool swallowRunExceptions)
	{
		var keyReader = new FakeKeyReader(typedInput.Select(ToKeyInfo).ToArray());
		var previousReader = ReplSessionIO.KeyReader;
		using var scope = ReplSessionIO.SetSession(writer, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = keyReader;
			ReplSessionIO.WindowSize = size;
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
			catch (InvalidOperationException ex) when (swallowRunExceptions)
			{
				// Only the expected ambient-failure exception type is captured for
				// assertion; any other type escapes as a genuine test failure.
				return ex;
			}

			return null;
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	/// <summary>
	/// The payload of the OSC mark starting at <paramref name="index"/>: everything up to
	/// its BEL terminator, so assertions cover exactly one mark without a magic length.
	/// </summary>
	private static string MarkPayloadAt(string raw, int index)
	{
		var bell = raw.IndexOf('\a', index);
		return bell < 0 ? raw[index..] : raw[index..bell];
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

	// Delegates to an inner writer but throws once the observed output contains the given
	// fragment (e.g. the command-end mark), simulating a transport torn down mid-command.
	// Every write shape funnels through the same rolling-window detector, so the fault
	// injection keeps firing even if the emitter changes its write granularity; tests
	// assert Threw so a silent stop of the injection cannot pass vacuously.
	private sealed class MarkFailingWriter(TextWriter inner, string failOn) : TextWriter
	{
		private readonly System.Text.StringBuilder _window = new();

		public bool Threw { get; private set; }

		public override System.Text.Encoding Encoding => inner.Encoding;

		public override void Write(char value)
		{
			ThrowIfFragmentObserved(value.ToString());
			inner.Write(value);
		}

		public override void Write(char[] buffer, int index, int count)
		{
			ThrowIfFragmentObserved(new string(buffer, index, count));
			inner.Write(buffer, index, count);
		}

		public override void Write(string? value)
		{
			ThrowIfFragmentObserved(value);
			inner.Write(value);
		}

		public override Task WriteAsync(char value)
		{
			ThrowIfFragmentObserved(value.ToString());
			return inner.WriteAsync(value);
		}

		public override Task WriteAsync(string? value)
		{
			ThrowIfFragmentObserved(value);
			return inner.WriteAsync(value);
		}

		public override Task WriteLineAsync(string? value)
		{
			ThrowIfFragmentObserved(value);
			return inner.WriteLineAsync(value);
		}

		private void ThrowIfFragmentObserved(string? value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return;
			}

			_window.Append(value);
			var overflow = _window.Length - (failOn.Length * 2);
			if (overflow > 0)
			{
				_window.Remove(0, overflow);
			}

			if (_window.ToString().Contains(failOn, StringComparison.Ordinal))
			{
				Threw = true;
				throw new IOException("simulated transport failure on mark write");
			}
		}
	}
}
