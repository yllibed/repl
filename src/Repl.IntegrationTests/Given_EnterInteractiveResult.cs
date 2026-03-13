namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_EnterInteractiveResult
{
	[TestMethod]
	[Description("Results.EnterInteractive() enters interactive mode after CLI command.")]
	public void When_CommandReturnsEnterInteractive_Then_ProcessEntersInteractiveMode()
	{
		var sut = ReplApp.Create();
		sut.Map("setup", () => Results.EnterInteractive());

		var output = ConsoleCaptureHelper.CaptureWithInput("exit\n", () => sut.Run(["setup", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("> ");
	}

	[TestMethod]
	[Description("Results.EnterInteractive(payload) renders payload then enters interactive mode.")]
	public void When_CommandReturnsEnterInteractiveWithPayload_Then_PayloadIsRenderedAndInteractiveModeStarts()
	{
		var sut = ReplApp.Create();
		sut.Map("setup", () => Results.EnterInteractive("Setup complete"));

		var output = ConsoleCaptureHelper.CaptureWithInput("exit\n", () => sut.Run(["setup", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Setup complete");
		output.Text.Should().Contain("> ");
	}

	[TestMethod]
	[Description("EnterInteractive as last tuple element enters interactive mode after rendering prior elements.")]
	public void When_TupleLastElementIsEnterInteractive_Then_PriorElementsRenderedAndInteractiveModeStarts()
	{
		var sut = ReplApp.Create();
		sut.Map("setup", () => (Results.Ok("Step 1 done"), Results.EnterInteractive()));

		var output = ConsoleCaptureHelper.CaptureWithInput("exit\n", () => sut.Run(["setup", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Step 1 done");
		output.Text.Should().Contain("> ");
	}

	[TestMethod]
	[Description("EnterInteractive with payload as last tuple element renders all payloads then enters interactive.")]
	public void When_TupleLastElementIsEnterInteractiveWithPayload_Then_AllPayloadsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("setup", () => (Results.Ok("Phase 1"), Results.EnterInteractive("Phase 2")));

		var output = ConsoleCaptureHelper.CaptureWithInput("exit\n", () => sut.Run(["setup", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Phase 1");
		output.Text.Should().Contain("Phase 2");
		output.Text.Should().Contain("> ");
	}

	[TestMethod]
	[Description("Results.EnterInteractive() factory creates correct type.")]
	public void When_CallingEnterInteractiveFactory_Then_ReturnsCorrectType()
	{
		var result = Results.EnterInteractive();

		result.Should().BeOfType<EnterInteractiveResult>();
		result.Payload.Should().BeNull();
	}

	[TestMethod]
	[Description("Results.EnterInteractive(payload) preserves the payload.")]
	public void When_CallingEnterInteractiveFactoryWithPayload_Then_PayloadIsPreserved()
	{
		var result = Results.EnterInteractive("hello");

		result.Should().BeOfType<EnterInteractiveResult>();
		result.Payload.Should().Be("hello");
	}
}
