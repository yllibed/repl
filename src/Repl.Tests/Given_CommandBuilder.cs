namespace Repl.Tests;

[TestClass]
public sealed class Given_CommandBuilder
{
	[TestMethod]
	[Description("Regression guard: verifies mapped commands are not protocol passthrough by default.")]
	public void When_CommandIsMapped_Then_ProtocolPassthroughIsDisabledByDefault()
	{
		var sut = CoreReplApp.Create();

		var command = sut.Map("status", () => "ok");

		command.IsProtocolPassthrough.Should().BeFalse();
	}

	[TestMethod]
	[Description("Regression guard: verifies fluent protocol passthrough API enables the route flag and preserves chaining.")]
	public void When_AsProtocolPassthroughIsCalled_Then_FlagIsEnabledAndBuilderIsReturned()
	{
		var sut = CoreReplApp.Create();
		var command = sut.Map("mcp start", () => Results.Exit(0));

		var chained = command.AsProtocolPassthrough();

		chained.Should().BeSameAs(command);
		command.IsProtocolPassthrough.Should().BeTrue();
	}

	[TestMethod]
	[Description("Regression guard: verifies shell completion bridge route is marked as protocol passthrough.")]
	public void When_ResolvingCompletionBridge_Then_CommandIsProtocolPassthrough()
	{
		var sut = CoreReplApp.Create();

		var match = sut.Resolve(
		[
			"completion",
			"__complete",
			"--shell",
			"bash",
			"--line",
			"repl c",
			"--cursor",
			"6",
		]);

		match.Should().NotBeNull();
		match!.Route.Command.IsProtocolPassthrough.Should().BeTrue();
	}
}
