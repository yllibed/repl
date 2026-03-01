namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_OptionParsingDiagnostics
{
	[TestMethod]
	[Description("Regression guard: verifies unknown command option fails in strict mode so typos are surfaced as validation errors instead of being silently accepted.")]
	public void When_UnknownCommandOptionInStrictMode_Then_ValidationErrorIsReturned()
	{
		var sut = ReplApp.Create();
		sut.Map("echo", (string text) => text);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "--txet", "hello", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Unknown option '--txet'");
		output.Text.Should().Contain("--text");
	}

	[TestMethod]
	[Description("Regression guard: verifies permissive mode keeps unknown options bindable so legacy handlers depending on implicit option names continue to work during migration.")]
	public void When_UnknownCommandOptionInPermissiveMode_Then_HandlerStillReceivesValue()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.AllowUnknownOptions = true);
		sut.Map("echo", (string mystery) => mystery);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", "--mystery", "hello", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("hello");
	}
}
