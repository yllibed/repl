namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_PromptTimeout
{
	[TestMethod]
	[Description("When a prompt has a timeout and input is redirected (empty), the default is selected.")]
	public void When_PromptTimesOutWithRedirectedInput_Then_DefaultIsSelected()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.UseDefault);
		sut.Map("choose", async (IReplInteractionChannel channel) =>
		{
			var index = await channel.AskChoiceAsync(
				"color",
				"Pick a color?",
				["Red", "Green", "Blue"],
				defaultIndex: 1,
				new AskOptions(Timeout: TimeSpan.FromMilliseconds(200))).ConfigureAwait(false);
			return index == 1 ? "green" : "other";
		});

		var output = ConsoleCaptureHelper.CaptureWithInput(string.Empty, () => sut.Run(["choose"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("green");
	}

	[TestMethod]
	[Description("AskOptions with explicit cancellation token is respected.")]
	public void When_AskOptionsHasExplicitToken_Then_TokenIsUsed()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.UseDefault);
		sut.Map("ask", async (IReplInteractionChannel channel) =>
		{
			var result = await channel.AskTextAsync(
				"name", "Name?", defaultValue: "default",
				new AskOptions(CancellationToken.None)).ConfigureAwait(false);
			return result;
		});

		var output = ConsoleCaptureHelper.CaptureWithInput(string.Empty, () => sut.Run(["ask"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("default");
	}

	[TestMethod]
	[Description("AskOptions with null options uses ambient token and behaves like before.")]
	public void When_AskOptionsIsNull_Then_AmbientTokenIsUsed()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.UseDefault);
		sut.Map("ask", async (IReplInteractionChannel channel) =>
		{
			var confirmed = await channel.AskConfirmationAsync(
				"ok", "Continue?", defaultValue: true).ConfigureAwait(false);
			return confirmed ? "yes" : "no";
		});

		var output = ConsoleCaptureHelper.CaptureWithInput(string.Empty, () => sut.Run(["ask"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("yes");
	}
}
