
namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_AttributeMetadataDiscovery
{
	[TestMethod]
	[Description("Regression guard: verifies handler has description attribute so that command help uses it.")]
	public void When_HandlerHasDescriptionAttribute_Then_CommandHelpUsesIt()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", ListContacts);

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "list", "--help"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Description: List all contacts");
	}

	[TestMethod]
	[Description("Regression guard: verifies handler has browsable false so that help hides command but invocation still works.")]
	public void When_HandlerHasBrowsableFalse_Then_HelpHidesCommandButInvocationStillWorks()
	{
		var sut = ReplApp.Create();
		sut.Map("debug secret", HiddenCommand);

		var help = ConsoleCaptureHelper.Capture(() => sut.Run(["--help"]));
		var execution = ConsoleCaptureHelper.Capture(() => sut.Run(["debug", "secret"]));

		help.ExitCode.Should().Be(0);
		help.Text.Should().NotContain("debug");
		execution.ExitCode.Should().Be(0);
		execution.Text.Should().Contain("hidden-ok");
	}

	[System.ComponentModel.Description("List all contacts")]
	private static string ListContacts() => "ok";

	[System.ComponentModel.Browsable(false)]
	private static string HiddenCommand() => "hidden-ok";
}









