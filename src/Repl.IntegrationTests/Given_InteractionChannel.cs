namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_InteractionChannel
{
	[TestMethod]
	[Description("Regression guard: verifies handler uses interaction channel status so that status is rendered.")]
	public void When_HandlerUsesInteractionChannelStatus_Then_StatusIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("import", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			await channel.WriteStatusAsync("Import started", ct).ConfigureAwait(false);
			return "done";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["import"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Import started");
		output.Text.Should().Contain("done");
	}

	[TestMethod]
	[Description("Regression guard: verifies percentage progress injection so that handlers can report progress through IProgress<double>.")]
	public void When_HandlerUsesInjectedPercentageProgress_Then_ProgressIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("sync", (IProgress<double> progress) =>
		{
			progress.Report(25);
			progress.Report(100);
			return "done";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["sync", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Progress: 25%");
		output.Text.Should().Contain("Progress: 100%");
	}

	[TestMethod]
	[Description("Regression guard: verifies structured progress injection so that handlers can report progress through IProgress<ReplProgressEvent>.")]
	public void When_HandlerUsesInjectedStructuredProgress_Then_ProgressIsRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("import", (IProgress<ReplProgressEvent> progress) =>
		{
			progress.Report(new ReplProgressEvent("Importing", Current: 3, Total: 4));
			return "ok";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["import", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Importing: 75%");
	}

	[TestMethod]
	[Description("Regression guard: verifies progress template and default label options are applied to injected progress rendering.")]
	public void When_ProgressTemplateAndDefaultLabelConfigured_Then_InjectedProgressUsesConfiguredFormatting()
	{
		var sut = ReplApp.Create().Options(o =>
		{
			o.Interaction.DefaultProgressLabel = "Sync";
			o.Interaction.ProgressTemplate = "[{label}] {percent:0.0}%";
		});
		sut.Map("sync", (IProgress<double> progress) =>
		{
			progress.Report(12.5);
			return "ok";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["sync", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("[Sync] 12.5%");
	}

	[TestMethod]
	[Description("Regression guard: verifies prompt fallback is fail and no answer provided so that command fails.")]
	public void When_PromptFallbackIsFailAndNoAnswerProvided_Then_CommandFails()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("confirm", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			_ = await channel.AskConfirmationAsync("overwrite", "Overwrite?", defaultValue: true).ConfigureAwait(false);
			return "ok";
		});

		var output = ConsoleCaptureHelper.CaptureWithInput(string.Empty, () => sut.Run(["confirm"]));

		output.ExitCode.Should().Be(1);
		output.Text.Should().Contain("Overwrite?");
	}

	[TestMethod]
	[Description("Regression guard: verifies prompt fallback uses default so that default answer is applied.")]
	public void When_PromptFallbackUsesDefault_Then_DefaultAnswerIsApplied()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.UseDefault);
		sut.Map("confirm", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var answer = await channel.AskConfirmationAsync("overwrite", "Overwrite?", defaultValue: true).ConfigureAwait(false);
			return answer ? "yes" : "no";
		});

		var output = ConsoleCaptureHelper.CaptureWithInput(string.Empty, () => sut.Run(["confirm"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("yes");
	}

	[TestMethod]
	[Description("Regression guard: verifies prefilled confirmation answer is provided so that prompt is not required in non-interactive mode.")]
	public void When_ConfirmationAnswerIsPrefilled_Then_PrefilledValueIsUsed()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("confirm", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var answer = await channel.AskConfirmationAsync("overwrite", "Overwrite?", defaultValue: false).ConfigureAwait(false);
			return answer ? "yes" : "no";
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["confirm", "--answer:overwrite=1"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("yes");
		output.Text.Should().NotContain("Overwrite?");
	}

	[TestMethod]
	[Description("Regression guard: verifies prefilled text answer is provided so that prompt fallback fail does not block execution.")]
	public void When_TextAnswerIsPrefilled_Then_CommandUsesPrefilledValue()
	{
		var sut = ReplApp.Create()
			.Options(o => o.Interaction.PromptFallback = PromptFallback.Fail);
		sut.Map("rename", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			var text = await channel.AskTextAsync("name", "Name?").ConfigureAwait(false);
			return text;
		});

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(["rename", "--answer:name=Alice"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Alice");
		output.Text.Should().NotContain("Name?");
	}
}
