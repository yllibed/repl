namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_ResponseFiles
{
	[TestMethod]
	[Description("Regression guard: verifies CLI mode expands @response files so multi-token command invocations can be externalized safely.")]
	public void When_RunningInCliMode_Then_ResponseFileIsExpanded()
	{
		var sut = ReplApp.Create();
		sut.Map("echo", (string text) => text);
		var responseFile = Path.Combine(Path.GetTempPath(), $"repl-response-{Guid.NewGuid():N}.rsp");
		File.WriteAllText(responseFile, "--text hello-from-rsp");

		try
		{
			var output = ConsoleCaptureHelper.Capture(() => sut.Run(["echo", $"@{responseFile}", "--no-logo"]));

			output.ExitCode.Should().Be(0);
			output.Text.Should().Contain("hello-from-rsp");
		}
		finally
		{
			File.Delete(responseFile);
		}
	}

	[TestMethod]
	[Description("Regression guard: verifies interactive command execution does not expand @response files by default so raw @tokens remain user-controlled in REPL sessions.")]
	public void When_RunningInInteractiveMode_Then_ResponseFileExpansionIsDisabledByDefault()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("echo", (string text) => text);
		var responseFile = Path.Combine(Path.GetTempPath(), $"repl-response-{Guid.NewGuid():N}.rsp");
		File.WriteAllText(responseFile, "--text hello-from-rsp");

		try
		{
			var output = ConsoleCaptureHelper.CaptureWithInput(
				$"echo @{responseFile}{Environment.NewLine}exit{Environment.NewLine}",
				() => sut.Run(["--interactive", "--no-logo"]));

			output.ExitCode.Should().Be(0);
			output.Text.Should().Contain($"@{responseFile}");
			output.Text.Should().NotContain("hello-from-rsp");
		}
		finally
		{
			File.Delete(responseFile);
		}
	}
}
