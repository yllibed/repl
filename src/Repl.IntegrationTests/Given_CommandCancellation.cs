namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_CommandCancellation
{
	[TestMethod]
	[Description("When a handler's session token is externally cancelled, the command is aborted.")]
	public async Task When_SessionTokenCancelled_Then_CommandAborted()
	{
		var sut = ReplApp.Create();
		sut.Map("slow", async (CancellationToken ct) =>
		{
			await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
			return "done";
		});

		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
		var output = await ConsoleCaptureHelper.CaptureAsync(async () =>
		{
			try
			{
				return await sut.RunAsync(["slow"], cts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return -1;
			}
		}).ConfigureAwait(false);

		output.ExitCode.Should().Be(-1);
	}

	[TestMethod]
	[Description("IReplKeyReader is registered and resolvable from the default service provider.")]
	public void When_HandlerRequestsIReplKeyReader_Then_ItIsInjected()
	{
		var sut = ReplApp.Create();
		IReplKeyReader? capturedReader = null;
		sut.Map("keys", (IReplKeyReader reader) =>
		{
			capturedReader = reader;
			return "ok";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["keys"]));

		output.ExitCode.Should().Be(0);
		capturedReader.Should().NotBeNull();
	}
}
