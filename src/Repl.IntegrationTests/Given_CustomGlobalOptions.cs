namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_CustomGlobalOptions
{
	[TestMethod]
	[Description("Regression guard: verifies custom global options are consumed before command parsing so strict command option validation does not reject recognized global tokens.")]
	public void When_CustomGlobalOptionIsProvided_Then_CommandStillExecutesInStrictMode()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("ping", () => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["ping", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ok");
	}

	[TestMethod]
	[Description("Regression guard: verifies global and command options with the same name are rejected so invocation behavior is never ambiguous.")]
	public void When_GlobalAndCommandOptionCollide_Then_ValidationErrorIsReturned()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.AddGlobalOption<string>("tenant"));
		sut.Map("ping", (string tenant) => tenant);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["ping", "--tenant", "acme", "--no-logo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Ambiguous option '--tenant'");
	}
}
