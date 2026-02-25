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
		output.Text.Should().Contain("1");
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

		var setOutput = ConsoleCaptureHelper.Capture(() => sut.Run(["set"]));
		var getOutput = ConsoleCaptureHelper.Capture(() => sut.Run(["get"]));

		setOutput.ExitCode.Should().Be(0);
		getOutput.ExitCode.Should().Be(0);
		getOutput.Text.Should().Contain("0");
	}
}
