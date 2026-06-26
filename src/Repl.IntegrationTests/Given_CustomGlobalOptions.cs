using Repl.Parameters;

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

	[TestMethod]
	[Description("Regression guard: verifies root help includes custom global options so registered global flags remain discoverable to users.")]
	public void When_RequestingRootHelp_Then_CustomGlobalOptionIsListedInGlobalOptionsSection()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.AddGlobalOption<string>("tenant", aliases: ["-t"]));
		sut.Map("ping", () => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Global Options:");
		output.Text.Should().Contain("--tenant, -t");
		output.Text.Should().Contain("Custom global option.");
	}

	[TestMethod]
	[Description("Regression guard: verifies typed global option descriptions are rendered in root help without adding default-value display.")]
	public void When_RequestingRootHelpForTypedGlobalOptions_Then_DescriptionsAreListed()
	{
		var sut = ReplApp.Create()
			.UseGlobalOptions<DemoGlobals>();
		sut.Map("status", (DemoGlobals globals) => globals.Tenant);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("--tenant, -t");
		output.Text.Should().Contain("Tenant id used for all commands.");
		output.Text.Should().Contain("--verbose, -v");
		output.Text.Should().Contain("Enable verbose diagnostics for all commands.");
		output.Text.Should().NotContain("[default:");
	}

	[TestMethod]
	[Description("Regression guard: verifies explicit global option descriptions are rendered in root help.")]
	public void When_RequestingRootHelpForExplicitGlobalOption_Then_DescriptionIsListed()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.AddGlobalOption<string>(
				"tenant",
				description: "Tenant id used for all commands.",
				aliases: ["-t"]));
		sut.Map("ping", () => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("--tenant, -t");
		output.Text.Should().Contain("Tenant id used for all commands.");
		output.Text.Should().NotContain("[default:");
	}

	private sealed class DemoGlobals
	{
		[System.ComponentModel.Description("Tenant id used for all commands.")]
		[ReplOption(Aliases = ["-t"])]
		public string? Tenant { get; set; } = "default";

		[System.ComponentModel.Description("Enable verbose diagnostics for all commands.")]
		[ReplOption(Aliases = ["-v"])]
		public bool Verbose { get; set; }
	}
}
