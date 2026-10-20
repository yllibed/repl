using System.Text.Json;
using Repl.Testing;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_FeedbackStream
{
	[TestMethod]
	[Description("Verifies a program reading JSON from standard output receives the payload alone, with feedback on standard error.")]
	public void When_OutputIsJson_Then_FeedbackGoesToStandardError()
	{
		var sut = ReplApp.Create();
		sut.Map("sync", async (IReplInteractionChannel channel, IProgress<double> progress, CancellationToken ct) =>
		{
			await channel.WriteStatusAsync("Sync started", ct).ConfigureAwait(false);
			await channel.WriteNoticeAsync("Connection established", ct).ConfigureAwait(false);
			await channel.WriteWarningAsync("Cache is warming up", ct).ConfigureAwait(false);
			progress.Report(25);
			return new { Count = 3 };
		});

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(() => sut.Run(["sync", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		using (var payload = JsonDocument.Parse(output.StdOut))
		{
			payload.RootElement.GetProperty("count").GetInt32().Should().Be(3);
		}

		output.StdErr.Should().Contain("Sync started");
		output.StdErr.Should().Contain("Connection established");
		output.StdErr.Should().Contain("Warning: Cache is warming up");
		output.StdErr.Should().Contain("Progress: 25%");
	}

	[TestMethod]
	[Description("Verifies the output format alone moves the feedback: inside a session the process's own redirection is ignored, so only --json can send it to the error writer.")]
	[DataRow("--json", true)]
	[DataRow("--human", false)]
	public void When_OnlyTheFormatDiffers_Then_OnlyAMachineFormatMovesTheFeedback(string formatFlag, bool expectedOnError)
	{
		var sut = ReplApp.Create();
		sut.Map("sync", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			await channel.WriteStatusAsync("Sync started", ct).ConfigureAwait(false);
			return new { Count = 3 };
		});
		using var output = new StringWriter();
		using var error = new StringWriter();

		int exitCode;
		using (ReplSessionIO.SetSession(output, TextReader.Null, error: error, isHostedSession: false))
		{
			exitCode = sut.Run(["sync", formatFlag, "--no-logo"]);
		}

		exitCode.Should().Be(0);
		(expectedOnError ? error : output).ToString().Should().Contain("Sync started");
		(expectedOnError ? output : error).ToString().Should().NotContain("Sync started");
	}

	[TestMethod]
	[Description("Verifies FeedbackStream.Output keeps feedback on standard output even when the output is for a program.")]
	public void When_FeedbackStreamIsOutput_Then_FeedbackStaysOnStandardOutput()
	{
		var sut = ReplApp.Create().Options(o => o.Output.FeedbackStream = ReplFeedbackStream.Output);
		sut.Map("sync", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			await channel.WriteStatusAsync("Sync started", ct).ConfigureAwait(false);
			return new { Count = 3 };
		});

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(() => sut.Run(["sync", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.StdOut.Should().Contain("Sync started");
		output.StdErr.Should().NotContain("Sync started");
	}

	[TestMethod]
	[Description("Verifies FeedbackStream.Error moves human feedback to standard error, leaving standard output with the result alone. The in-place progress line is covered by Given_ConsoleReplInteractionPresenter_FeedbackStream, which does not depend on how the test host's streams are redirected.")]
	public void When_FeedbackStreamIsError_Then_HumanFeedbackGoesToStandardError()
	{
		var sut = ReplApp.Create().Options(o => o.Output.FeedbackStream = ReplFeedbackStream.Error);
		sut.Map("sync", async (IReplInteractionChannel channel, CancellationToken ct) =>
		{
			await channel.WriteProgressAsync("Syncing", 25, ct).ConfigureAwait(false);
			await channel.WriteStatusAsync("Almost there", ct).ConfigureAwait(false);
			return "done";
		});

		var output = ConsoleCaptureHelper.CaptureStdOutAndErr(() => sut.Run(["sync", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		output.StdOut.Should().Be($"done{Environment.NewLine}");
		output.StdErr.Should().Contain("Syncing: 25%");
		output.StdErr.Should().Contain("Almost there");
	}

	[TestMethod]
	[Description("Verifies the label of a console prompt follows the feedback to standard error. Input is redirected here, so nothing is echoed: the echo of a terminal user's typing is not routed.")]
	public void When_OutputIsJsonAndCommandPrompts_Then_PromptGoesToStandardError()
	{
		var sut = ReplApp.Create();
		sut.Map("greet", async (IReplInteractionChannel channel) =>
			await channel.AskTextAsync("name", "Your name").ConfigureAwait(false));

		var output = ConsoleCaptureHelper.CaptureWithInputStdOutAndErr(
			"Alice" + Environment.NewLine,
			() => sut.Run(["greet", "--json", "--no-logo"]));

		output.ExitCode.Should().Be(0);
		JsonSerializer.Deserialize<string>(output.StdOut).Should().Be("Alice");
		output.StdErr.Should().Contain("Your name");
	}

	[TestMethod]
	[Description("Regression guard: verifies a hosted session, which has a single stream, still shows feedback with a JSON payload.")]
	public async Task When_HostedSessionUsesJson_Then_FeedbackStaysInTheSessionStream()
	{
		await using var host = ReplTestHost.Create(() =>
		{
			var app = ReplApp.Create().UseDefaultInteractive();
			app.Map("sync", async (IReplInteractionChannel channel, CancellationToken ct) =>
			{
				await channel.WriteStatusAsync("Sync started", ct).ConfigureAwait(false);
				return new { Count = 3 };
			});
			return app;
		});
		await using var session = await host.OpenSessionAsync();

		var result = await session.RunCommandAsync("sync --json --no-logo");

		result.ExitCode.Should().Be(0);
		result.OutputText.Should().Contain("Sync started");
		result.OutputText.Should().Contain("\"count\": 3");
	}
}
