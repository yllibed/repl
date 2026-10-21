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

		output.ExitCode.Should().Be(2);
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
	[Description("Hidden(false) re-exposes a manually registered global option after it was hidden.")]
	public void When_HiddenGlobalOptionIsShownAgain_Then_RootHelpListsIt()
	{
		var sut = ReplApp.Create()
			.Options(options =>
			{
				options.Parsing.AddGlobalOption<string>("tenant");
				options.Parsing.GlobalOption("tenant").Hidden().Hidden(isHidden: false);
			});
		sut.Map("ping", () => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("--tenant");
	}

	[TestMethod]
	[Description("Selecting an unregistered global option fails clearly instead of silently creating ineffective metadata.")]
	public void When_SelectingUnknownGlobalOption_Then_ConfigurationThrows()
	{
		var parsing = new ParsingOptions();

		var act = () => parsing.GlobalOption("missing");

		act.Should().Throw<KeyNotFoundException>()
			.WithMessage("*missing*registered*");
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
				aliases: ["-t"],
				defaultValue: null,
				description: "Tenant id used for all commands."));
		sut.Map("ping", () => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["--help", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("--tenant, -t");
		output.Text.Should().Contain("Tenant id used for all commands.");
		output.Text.Should().NotContain("[default:");
	}

	[TestMethod]
	[Description("Verifies a global option holding a collection records no text default, whichever way it is registered: a collection cannot round-trip through one, so the accessor falls back to the caller's default instead of failing on a type name.")]
	[DataRow(true, DisplayName = "Typed global options")]
	[DataRow(false, DisplayName = "AddGlobalOption")]
	public void When_AGlobalOptionHoldsACollection_Then_TheAccessorRecordsNoTextDefault(bool typed)
	{
		var sut = typed
			? ReplApp.Create().UseGlobalOptions<TagGlobals>()
			: ReplApp.Create().Options(options => options.Parsing.AddGlobalOption<string[]>("tags", defaultValue: ["a", "b"]));
		sut.Map("status", (IGlobalOptionsAccessor globals) =>
			string.Join('|', globals.GetValue<string[]>("tags") ?? ["none"]));

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["status", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Trim().Should().Be("none");
	}

	[TestMethod]
	[Description("Verifies a global option declared as a collection of characters records no text default even though its default is a string: whether a default round-trips is judged from the declared type.")]
	[DataRow(true, DisplayName = "Typed global options")]
	[DataRow(false, DisplayName = "AddGlobalOption")]
	public void When_AGlobalOptionIsACollectionOfCharacters_Then_ItsStringDefaultIsNotRecorded(bool typed)
	{
		var sut = typed
			? ReplApp.Create().UseGlobalOptions<LetterGlobals>()
			: ReplApp.Create().Options(options => options.Parsing.AddGlobalOption<IEnumerable<char>>("letters", defaultValue: "abc"));
		sut.Map("status", (IGlobalOptionsAccessor globals) =>
			globals.GetValue<IEnumerable<char>>("letters") is { } letters ? string.Concat(letters) : "none");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["status", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Trim().Should().Be("none");
	}

	private sealed class TagGlobals
	{
		public string[] Tags { get; set; } = ["a", "b"];
	}

	private sealed class LetterGlobals
	{
		public IEnumerable<char> Letters { get; set; } = "abc";
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
