namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_CommandAliases
{
	[TestMethod]
	[Description("Regression guard: verifies command defines alias so that alias invokes handler.")]
	public void When_CommandDefinesAlias_Then_AliasInvokesHandler()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok")
			.WithAlias("ls");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "ls"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ok");
	}

	[TestMethod]
	[Description("Regression guard: verifies alias does not match terminal segment so that command is not resolved.")]
	public void When_AliasDoesNotMatchTerminalSegment_Then_CommandIsNotResolved()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok")
			.WithAlias("ls");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["ls", "list"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Unknown command");
	}

	[TestMethod]
	[Description("Regression guard: verifies alias has unique prefix so that prefix resolves to aliased command.")]
	public void When_AliasHasUniquePrefix_Then_PrefixResolvesToAliasedCommand()
	{
		var sut = ReplApp.Create();
		sut.Map("contact list", () => "ok")
			.WithAlias("zz");

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["contact", "z"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ok");
	}
}


