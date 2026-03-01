using System.Globalization;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_Completions
{
	[TestMethod]
	[Description("Regression guard: verifies interactive complete command is used so that completion provider candidates are rendered.")]
	public void When_InteractiveCompleteCommandIsUsed_Then_CompletionProviderCandidatesAreRendered()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("contact inspect", () => "ok")
			.WithCompletion("clientId", static (_, input, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>(
					[$"{input}001", $"{input}002"]));

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"complete contact inspect --target clientId --input ab\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("ab001");
		output.Text.Should().Contain("ab002");
	}

	[TestMethod]
	[Description("Regression guard: verifies interactive complete command uses unknown target so that error is rendered.")]
	public void When_InteractiveCompleteCommandUsesUnknownTarget_Then_ErrorIsRendered()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("contact inspect", () => "ok");

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"complete contact inspect --target missing\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Error: no completion provider registered for 'missing'.");
	}

	[TestMethod]
	[Description("Regression guard: verifies cli complete command is used so that completion provider candidates are rendered.")]
	public void When_CliCompleteCommandIsUsed_Then_CompletionProviderCandidatesAreRendered()
	{
		var sut = ReplApp.Create();
		sut.Map("contact inspect", () => "ok")
			.WithCompletion("clientId", static (_, input, _) =>
				ValueTask.FromResult<IReadOnlyList<string>>([$"{input}A", $"{input}B"]));

		var output = ConsoleCaptureHelper.Capture(() =>
			sut.Run(["complete", "contact", "inspect", "--target", "clientId", "--input", "x"]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("xA");
		output.Text.Should().Contain("xB");
	}

	[TestMethod]
	[Description("Regression guard: verifies interactive autocomplete show command reports configured and effective mode.")]
	public void When_AutocompleteShowCommandIsUsed_Then_ModeSummaryIsRendered()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("ping", () => "pong");

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"autocomplete show\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Autocomplete mode:");
	}

	[TestMethod]
	[Description("Regression guard: verifies interactive autocomplete mode command stores a session override.")]
	public void When_AutocompleteModeCommandIsUsed_Then_SessionOverrideIsApplied()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("ping", () => "pong");

		var output = ConsoleCaptureHelper.CaptureWithInput(
			"autocomplete mode off\nautocomplete show\nexit\n",
			() => sut.Run([]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("Autocomplete mode set to Off");
		output.Text.Should().Contain("override=Off");
	}

	[TestMethod]
	[Description("Regression guard: verifies shell completion suggests custom global options so app-registered globals remain discoverable from tab completion.")]
	public void When_CompletingGlobalPrefix_Then_CustomGlobalOptionsAreSuggested()
	{
		var sut = ReplApp.Create()
			.Options(options => options.Parsing.AddGlobalOption<string>("tenant", aliases: ["-t"]));
		sut.Map("ping", () => "pong");
		const string line = "repl --te";

		var output = ConsoleCaptureHelper.Capture(() => sut.Run(
		[
			"completion",
			"__complete",
			"--shell",
			"bash",
			"--line",
			line,
			"--cursor",
			line.Length.ToString(CultureInfo.InvariantCulture),
			"--no-logo",
		]));

		output.ExitCode.Should().Be(0);
		output.Text.Should().Contain("--tenant");
	}
}


