namespace Repl.IntegrationTests;

[TestClass]
public sealed class Given_InvocationBridge
{
	[TestMethod]
	public void When_RealShellBridgeIsHandled_Then_TheHookRunsOnceAndStdoutContainsNoPayload()
	{
		var result = ShellCompletionTestHostRunner.Run("invocation-hook",
			["completion", "__complete", "--shell", "bash", "--line", "repl c", "--cursor", "6"]);
		result.ExitCode.Should().Be(0, result.Text);
		result.Text.Should().BeNullOrWhiteSpace();
	}
}
