using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
	[Description("Verifies feedback written before the command is resolved — by an argument preprocessor, an invocation hook, a hook that answers the invocation itself, a startup hook, or a preprocessor or hook after an earlier one rewrote the invocation into JSON — also leaves a machine-readable payload alone.")]
	[DataRow(HookKind.Preprocessor, DisplayName = "Argument preprocessor")]
	[DataRow(HookKind.PreprocessorAfterRewrite, DisplayName = "Argument preprocessor after another rewrote the invocation into JSON")]
	[DataRow(HookKind.Invocation, DisplayName = "Invocation hook")]
	[DataRow(HookKind.Handled, DisplayName = "Invocation hook that handles the invocation")]
	[DataRow(HookKind.Startup, DisplayName = "Startup hook")]
	[DataRow(HookKind.AfterRewrite, DisplayName = "Invocation hook after another rewrote the invocation into JSON")]
	public void When_FeedbackIsWrittenBeforeTheCommandIsResolvedForJsonOutput_Then_ItGoesToTheErrorWriter(HookKind hook)
	{
		var sut = ReplApp.Create();
		sut.Map("sync", () => new { Count = 3 });
		var args = new[] { "sync", "--json", "--no-logo" };
		switch (hook)
		{
			case HookKind.Preprocessor:
				sut.UseArgumentPreprocessor(WriteStatusFromPreprocessor(sut));
				break;
			case HookKind.PreprocessorAfterRewrite:
				args = ["sync", "--no-logo"];
				sut.UseArgumentPreprocessor((input, _) => ArgumentPreprocessingResult.Replace([.. input.Arguments, "--json"]));
				sut.UseArgumentPreprocessor(WriteStatusFromPreprocessor(sut));
				break;
			case HookKind.Invocation:
				sut.UseInvocationHook(WriteStatusAndContinueAsync);
				break;
			case HookKind.Handled:
				sut.UseInvocationHook(async (IReplInteractionChannel channel, CancellationToken ct) =>
				{
					await channel.WriteStatusAsync("Hook ran", ct).ConfigureAwait(false);
					return ReplInvocationResult.Handled(new { Count = 3 });
				});
				break;
			case HookKind.Startup:
				sut.UseStartupHook(async (IReplInteractionChannel channel, CancellationToken ct) =>
				{
					await channel.WriteStatusAsync("Hook ran", ct).ConfigureAwait(false);
					return ArgumentPreprocessingResult.Continue();
				});
				break;
			case HookKind.AfterRewrite:
				args = ["sync", "--no-logo"];
				sut.UseInvocationHook(() =>
					ReplInvocationResult.Replace(ReplInvocationRequest.FromArguments(["sync", "--json", "--no-logo"])));
				sut.UseInvocationHook(WriteStatusAndContinueAsync);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(hook), hook, message: null);
		}

		using var output = new StringWriter();
		using var error = new StringWriter();
		int exitCode;
		using (ReplSessionIO.SetSession(output, TextReader.Null, error: error, isHostedSession: false))
		{
			exitCode = sut.Run(args);
		}

		exitCode.Should().Be(0);
		error.ToString().Should().Contain("Hook ran");
		using var payload = JsonDocument.Parse(output.ToString());
		payload.RootElement.GetProperty("count").GetInt32().Should().Be(3);
	}

	[TestMethod]
	[Description("Verifies a preprocessor's feedback follows the arguments it receives: once an earlier preprocessor rewrote --json away, the feedback stays beside the human output.")]
	public void When_AnEarlierPreprocessorDropsJson_Then_FeedbackStaysOnTheOutputWriter()
	{
		var sut = ReplApp.Create();
		sut.Map("sync", () => "done");
		sut.UseArgumentPreprocessor((input, _) => ArgumentPreprocessingResult.Replace(
			[.. input.Arguments.Where(static argument => !string.Equals(argument, "--json", StringComparison.Ordinal))]));
		sut.UseArgumentPreprocessor(WriteStatusFromPreprocessor(sut));

		using var output = new StringWriter();
		using var error = new StringWriter();
		int exitCode;
		using (ReplSessionIO.SetSession(output, TextReader.Null, error: error, isHostedSession: false))
		{
			exitCode = sut.Run(["sync", "--json", "--no-logo"]);
		}

		exitCode.Should().Be(0);
		output.ToString().Should().Contain("Hook ran");
		error.ToString().Should().NotContain("Hook ran");
	}

	[TestMethod]
	[Description("Verifies an interactive command that asks for JSON keeps its invocation hook's feedback out of the payload, as a one-shot run does.")]
	public void When_AnInteractiveHookWritesFeedbackForJsonOutput_Then_ItGoesToTheErrorWriter()
	{
		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("sync", () => new { Count = 3 });
		sut.UseInvocationHook(WriteStatusAndContinueAsync);

		using var output = new StringWriter();
		using var error = new StringWriter();
		using (ReplSessionIO.SetSession(output, new StringReader($"sync --json{Environment.NewLine}exit{Environment.NewLine}"), error: error, isHostedSession: false))
		{
			_ = sut.Run(["--interactive", "--no-logo"]);
		}

		// The launch and the exit command run the hook too, in human format, so their feedback stays on the
		// output; only the JSON command's moves, and nothing may sit between its prompt and its payload.
		var text = output.ToString();
		var payloadStart = text.IndexOf('{', StringComparison.Ordinal);
		var promptEnd = text.LastIndexOf('>', payloadStart) + 1;
		text[promptEnd..payloadStart].Should().BeNullOrWhiteSpace();
		error.ToString().Should().Contain("Hook ran");
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

	public enum HookKind
	{
		Preprocessor,
		PreprocessorAfterRewrite,
		Invocation,
		Handled,
		Startup,
		AfterRewrite,
	}

	// A preprocessor is given no services, so it reaches the channel the way an app capturing it would.
	private static Func<ArgumentPreprocessingContext, CancellationToken, ValueTask<ArgumentPreprocessingResult>> WriteStatusFromPreprocessor(ReplApp app) =>
		async (_, ct) =>
		{
			await app.Core.CurrentServiceProvider.GetRequiredService<IReplInteractionChannel>()
				.WriteStatusAsync("Hook ran", ct).ConfigureAwait(false);
			return ArgumentPreprocessingResult.Continue();
		};

	private static async ValueTask<ReplInvocationResult> WriteStatusAndContinueAsync(IReplInteractionChannel channel, CancellationToken ct)
	{
		await channel.WriteStatusAsync("Hook ran", ct).ConfigureAwait(false);
		return ReplInvocationResult.Continue();
	}
}
