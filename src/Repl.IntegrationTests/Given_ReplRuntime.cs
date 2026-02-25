namespace Repl.IntegrationTests;

[TestClass]
public sealed class Given_ReplRuntime
{
	[TestMethod]
	[Description("Regression guard: verifies running known command so that exit code is zero.")]
	public void When_RunningKnownCommand_Then_ExitCodeIsZero()
	{
		var sut = ReplApp.Create()
			.UseDefaultInteractive()
			.UseCliProfile();

		sut.Map("hello", () => "world");

		var exitCode = sut.Run(["hello"]);

		exitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("Regression guard: verifies running async with cancelled token so that cancellation is observed.")]
	public async Task When_RunningAsyncWithCancelledToken_Then_CancellationIsObserved()
	{
		var sut = ReplApp.Create();
		using var cancellationTokenSource = new CancellationTokenSource();
		await cancellationTokenSource.CancelAsync().ConfigureAwait(false);

		var action = async () =>
			await sut.RunAsync([], cancellationTokenSource.Token).ConfigureAwait(false);

		await action.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Regression guard: verifies applying embedded profile so that exit ambient command is disabled.")]
	public void When_ApplyingEmbeddedProfile_Then_ExitAmbientCommandIsDisabled()
	{
		var sut = ReplApp.Create().UseEmbeddedConsoleProfile();
		var isExitEnabled = true;

		sut.Options(options => isExitEnabled = options.AmbientCommands.ExitCommandEnabled);

		isExitEnabled.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies running without args so that interactive session starts at root.")]
	public void When_RunningWithoutArgs_Then_InteractiveSessionStartsAtRoot()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();

		var output = ConsoleCaptureHelper.CaptureWithInput("exit\n", () => sut.Run([]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain(">");
	}

	[TestMethod]
	[Description("Regression guard: verifies running complete command with interactive flag so that command executes and session stays alive.")]
	public void When_RunningCompleteCommandWithInteractiveFlag_Then_CommandExecutesAndSessionStaysAlive()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("contact {id:int} show", (int id) => id);

		var output = ConsoleCaptureHelper.CaptureWithInput("exit\n", () => sut.Run(["contact", "42", "show", "--interactive"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("42");
		output.Text.Should().Contain(">");
	}

	[TestMethod]
	[Description("Regression guard: verifies running exit command in one-shot mode so that ambient exit is honored and unknown command is not emitted.")]
	public void When_RunningExitCommandInNonInteractiveMode_Then_ExitCodeIsZeroWithoutUnknownCommand()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["exit", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().NotContain("Unknown command");
	}

	[TestMethod]
	[Description("Regression guard: verifies running exit command when disabled in one-shot mode so that user gets explicit failure.")]
	public void When_RunningExitCommandInNonInteractiveModeAndExitDisabled_Then_ExitCodeIsOneWithError()
	{
		var sut = ReplApp.Create();
		sut.Options(options => options.AmbientCommands.ExitCommandEnabled = false);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["exit", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Error: exit command is disabled.");
	}

	[TestMethod]
	[Description("Regression guard: verifies using '..' in one-shot mode so that interactive-only ambient command fails explicitly.")]
	public void When_RunningUpAmbientCommandInNonInteractiveMode_Then_ExitCodeIsOneWithModeError()
	{
		var sut = ReplApp.Create();

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["..", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Error: '..' is available only in interactive mode.");
	}

}






