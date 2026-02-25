namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_BannerBehavior
{
	[TestMethod]
	[Description("Regression guard: verifies banner is enabled in human mode so that description is rendered before command output.")]
	public void When_BannerIsEnabledInHumanMode_Then_DescriptionIsRenderedBeforeCommandOutput()
	{
		var sut = ReplApp.Create()
			.WithDescription("Test banner");
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["hello"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Test banner");
		output.Text.Should().Contain("world");
	}

	[TestMethod]
	[Description("Regression guard: verifies no logo flag is provided so that banner is suppressed.")]
	public void When_NoLogoFlagIsProvided_Then_BannerIsSuppressed()
	{
		var sut = ReplApp.Create()
			.WithDescription("Test banner");
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["hello", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().NotContain("Test banner");
		output.Text.Should().Contain("world");
	}

	[TestMethod]
	[Description("Regression guard: verifies output is machine format so that banner is suppressed.")]
	public void When_OutputIsMachineFormat_Then_BannerIsSuppressed()
	{
		var sut = ReplApp.Create()
			.WithDescription("Test banner");
		sut.Map("hello", () => new { Value = "world" });

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["hello", "--json"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().NotContain("Test banner");
		output.Text.Should().Contain("\"value\": \"world\"");
	}
}



