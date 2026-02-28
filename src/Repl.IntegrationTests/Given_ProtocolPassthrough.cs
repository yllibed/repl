namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ProtocolPassthrough
{
	[TestMethod]
	[Description("Regression guard: verifies protocol passthrough suppresses banners so stdout only contains handler protocol output.")]
	public void When_CommandIsProtocolPassthrough_Then_GlobalAndCommandBannersAreSuppressed()
	{
		var sut = ReplApp.Create()
			.WithDescription("Test banner");
		sut.Map(
				"mcp start",
				() =>
				{
					Console.Out.WriteLine("rpc-ready");
					return Results.Exit(0);
				})
			.WithBanner("Command banner")
			.AsProtocolPassthrough();

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(() => sut.Run(["mcp", "start"]));

		output.ExitCode.Should().Be(0);
		output.StdOut.Should().Contain("rpc-ready");
		output.StdOut.Should().NotContain("Test banner");
		output.StdOut.Should().NotContain("Command banner");
		output.StdErr.Should().BeNullOrWhiteSpace();
	}

	[TestMethod]
	[Description("Regression guard: verifies IReplIoContext output stays on stdout in CLI protocol passthrough while framework output remains redirected.")]
	public void When_ProtocolPassthroughHandlerUsesIoContext_Then_OutputIsWrittenToStdOut()
	{
		var sut = ReplApp.Create()
			.WithDescription("Test banner");
		sut.Map(
				"mcp start",
				(IReplIoContext io) =>
				{
					io.Output.WriteLine("rpc-ready");
					return Results.Exit(0);
				})
			.WithBanner("Command banner")
			.AsProtocolPassthrough();

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(() => sut.Run(["mcp", "start"]));

		output.ExitCode.Should().Be(0);
		output.StdOut.Should().Contain("rpc-ready");
		output.StdOut.Should().NotContain("Test banner");
		output.StdOut.Should().NotContain("Command banner");
		output.StdErr.Should().BeNullOrWhiteSpace();
	}

	[TestMethod]
	[Description("Regression guard: verifies protocol passthrough routes repl diagnostics to stderr while keeping stdout clean.")]
	public void When_ProtocolPassthroughHandlerFails_Then_ReplDiagnosticsAreWrittenToStderr()
	{
		var sut = ReplApp.Create()
			.WithDescription("Test banner");
		sut.Map(
				"mcp start",
				static string () => throw new InvalidOperationException("invalid start"))
			.AsProtocolPassthrough();

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(() => sut.Run(["mcp", "start"]));

		output.ExitCode.Should().Be(1);
		output.StdOut.Should().BeNullOrWhiteSpace();
		output.StdErr.Should().Contain("Validation: invalid start");
		output.StdErr.Should().NotContain("Test banner");
	}

	[TestMethod]
	[Description("Regression guard: verifies shell completion bridge protocol errors are rendered by framework on stderr in passthrough mode.")]
	public void When_CompletionBridgeUsageIsInvalid_Then_ErrorIsWrittenToStderr()
	{
		var sut = ReplApp.Create();

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(
			() => sut.Run(["completion", "__complete", "--shell", "bash", "--line", "repl ping", "--cursor", "invalid"]));

		output.ExitCode.Should().Be(1);
		output.StdOut.Should().BeNullOrWhiteSpace();
		output.StdErr.Should().Contain("usage: completion __complete");
	}

	[TestMethod]
	[Description("Regression guard: verifies protocol passthrough keeps explicit exit results silent while preserving the exit code.")]
	public void When_ProtocolPassthroughReturnsExitWithoutPayload_Then_ExitCodeIsPropagatedWithoutFrameworkOutput()
	{
		var sut = ReplApp.Create()
			.WithDescription("Test banner");
		sut.Map("mcp start", () => Results.Exit(7))
			.AsProtocolPassthrough();

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(() => sut.Run(["mcp", "start"]));

		output.ExitCode.Should().Be(7);
		output.StdOut.Should().BeNullOrWhiteSpace();
		output.StdErr.Should().BeNullOrWhiteSpace();
	}

	[TestMethod]
	[Description("Regression guard: verifies protocol passthrough ignores interactive follow-up so stdin remains available to the handler lifecycle.")]
	public void When_ProtocolPassthroughIsInvokedWithInteractiveFlag_Then_InteractiveLoopIsNotStarted()
	{
		var sut = ReplApp.Create()
			.WithDescription("Test banner");
		sut.Map("mcp start", () => Results.Exit(0))
			.AsProtocolPassthrough();

		var output = ConsoleCaptureHelper.CaptureWithInputStdOutAndErr(
			"exit\n",
			() => sut.Run(["mcp", "start", "--interactive"]));

		output.ExitCode.Should().Be(0);
		output.StdOut.Should().BeNullOrWhiteSpace();
		output.StdErr.Should().BeNullOrWhiteSpace();
	}

	[TestMethod]
	[Description("Regression guard: verifies protocol passthrough fails fast in hosted sessions when handler is console-bound.")]
	public void When_ProtocolPassthroughRunsInHostedSessionWithoutIoContext_Then_RuntimeReturnsExplicitError()
	{
		var sut = ReplApp.Create();
		sut.Map("mcp start", () => Results.Exit(0))
			.AsProtocolPassthrough();
		using var input = new StringReader(string.Empty);
		using var output = new StringWriter();
		var host = new InMemoryHost(input, output);

		var exitCode = sut.Run(
			["mcp", "start"],
			host,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.None });

		exitCode.Should().Be(1);
		output.ToString().Should().Contain("protocol passthrough");
		output.ToString().Should().Contain("IReplIoContext");
	}

	[TestMethod]
	[Description("Regression guard: verifies hosted protocol passthrough works when handler requests IReplIoContext streams explicitly.")]
	public void When_ProtocolPassthroughRunsInHostedSessionWithIoContext_Then_HandlerCanWriteToSessionStream()
	{
		var sut = ReplApp.Create();
		sut.Map(
				"transfer send",
				(IReplIoContext io) =>
				{
					io.Output.WriteLine("zmodem-start");
					return Results.Exit(0);
				})
			.AsProtocolPassthrough();
		using var input = new StringReader(string.Empty);
		using var output = new StringWriter();
		var host = new InMemoryHost(input, output);

		var exitCode = sut.Run(
			["transfer", "send"],
			host,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.None });

		exitCode.Should().Be(0);
		output.ToString().Should().Contain("zmodem-start");
	}

	[TestMethod]
	[Description("Regression guard: verifies IReplIoContext is injectable in normal CLI execution.")]
	public void When_HandlerRequestsIoContextInCli_Then_RuntimeInjectsConsoleContext()
	{
		var sut = ReplApp.Create();
		sut.Map("io check", (IReplIoContext io) => io.IsHostedSession ? "hosted" : "local");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["io", "check", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("local");
	}

	private sealed class InMemoryHost(TextReader input, TextWriter output) : IReplHost
	{
		public TextReader Input { get; } = input;

		public TextWriter Output { get; } = output;
	}
}
