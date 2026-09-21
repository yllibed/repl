namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_SessionState
{
	[TestMethod]
	[Description("Regression guard: verifies interactive session reuses one session state so that values persist across commands.")]
	public void When_RunningInteractiveSession_Then_SessionStatePersistsAcrossCommands()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("set", (IReplSessionState state) =>
		{
			var next = state.Get<int>("counter") + 1;
			state.Set("counter", next);
			return next;
		});
		sut.Map("get", (IReplSessionState state) => state.Get<int>("counter"));

		var output = ConsoleCaptureHelper.CaptureWithInput("set\nget\nexit\n", () => sut.Run([]));

		output.ExitCode.Should().Be(0);
		// The banner carries a version, so a bare Contain("1") passes without either command running.
		// Counting rendered results discriminates instead: "set" prints 1, and "get" prints 1 only
		// because the state persisted — without persistence it prints 0 and the count drops to one.
		var results = output.Text
			.Split('\n')
			.Select(static line => line.Trim().TrimStart('>').Trim())
			.Count(static value => string.Equals(value, "1", StringComparison.Ordinal));

		results.Should().Be(2, "both the write and the later read report the same counter");
	}

	[TestMethod]
	[Description("Regression guard: verifies cli invocations create isolated session state so that values do not leak between runs.")]
	public void When_RunningCliInvocations_Then_SessionStateIsIsolatedPerRun()
	{
		var sut = ReplApp.Create();
		sut.Map("set", (IReplSessionState state) =>
		{
			state.Set("counter", 42);
			return "set";
		});
		sut.Map("get", (IReplSessionState state) => state.Get<int>("counter"));

		var setOutput = ConsoleCaptureHelper.Capture(() => sut.Run(["set", "--no-logo"]));
		var getOutput = ConsoleCaptureHelper.Capture(() => sut.Run(["get", "--no-logo"]));

		setOutput.ExitCode.Should().Be(0);
		getOutput.ExitCode.Should().Be(0);
		// Exact, and with the banner suppressed: Contain("0") against un-suppressed output is satisfied
		// by the version line alone, which is how this guard stayed green through the defect it names.
		getOutput.Text.Trim().Should().Be("0");
	}
}
