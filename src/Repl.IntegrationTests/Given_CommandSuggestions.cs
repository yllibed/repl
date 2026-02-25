namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_CommandSuggestions
{
	[TestMethod]
	[Description("Regression guard: verifies command has unique prefix so that prefix executes command.")]
	public void When_CommandHasUniquePrefix_Then_PrefixExecutesCommand()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["cont", "lis"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ok");
	}

	[TestMethod]
	[Description("Regression guard: verifies command prefix is ambiguous so that framework returns validation error.")]
	public void When_CommandPrefixIsAmbiguous_Then_FrameworkReturnsValidationError()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "list");
		sut.Map("contact load", () => "load");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "l"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Ambiguous command prefix 'l'.");
		output.Text.Should().Contain("list");
		output.Text.Should().Contain("load");
	}

	[TestMethod]
	[Description("Regression guard: verifies cli command is unknown so that error includes did you mean suggestion.")]
	public void When_CliCommandIsUnknown_Then_ErrorIncludesDidYouMeanSuggestion()
	{
		var sut = ReplApp.Create();
		sut.Map("hello", () => "world");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["helo"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Unknown command 'helo'.");
		output.Text.Should().Contain("Did you mean 'hello'?");
	}

	[TestMethod]
	[Description("Regression guard: verifies best suggestion is hidden command so that hidden command is not exposed.")]
	public void When_BestSuggestionIsHiddenCommand_Then_HiddenCommandIsNotExposed()
	{
		var sut = ReplApp.Create();
		sut.Map("hello", () => "world");
		sut.Map("helpme", () => "secret").Hidden();

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["helpm"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Unknown command 'helpm'.");
		output.Text.Should().NotContain("helpme");
	}

	[TestMethod]
	[Description("Regression guard: verifies prefix would resolve only to hidden command so that hidden command is not invoked by prefix.")]
	public void When_PrefixWouldResolveOnlyToHiddenCommand_Then_HiddenCommandIsNotInvokedByPrefix()
	{
		var sut = ReplApp.Create();
		sut.Map("hello", () => "world");
		sut.Map("helpme", () => "secret").Hidden();

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["help"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Unknown command 'help'.");
		output.Text.Should().NotContain("secret");
	}
}






