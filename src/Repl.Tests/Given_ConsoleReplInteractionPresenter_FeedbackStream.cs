using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

/// <summary>
/// Drives the presenter inside a non-hosted session with distinct output and error writers. A session
/// makes both streams count as terminals regardless of how the test host's own streams are redirected,
/// so the in-place progress line is exercised deterministically.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class Given_ConsoleReplInteractionPresenter_FeedbackStream
{
	[TestMethod]
	[Description("Verifies a progress line opened on standard error is closed there, leaving standard output untouched.")]
	public async Task When_ProgressLineIsOpenOnError_Then_TheNextFeedbackClosesItOnError()
	{
		var presenter = CreatePresenter(ReplFeedbackStream.Error);
		using var output = new StringWriter();
		using var error = new StringWriter();
		using var session = OpenSession(output, error);

		await presenter.PresentAsync(new ReplProgressEvent("Syncing", Percent: 25), CancellationToken.None);
		await presenter.PresentAsync(new ReplStatusEvent("Almost there"), CancellationToken.None);

		output.ToString().Should().BeEmpty();
		error.ToString().Should().Be($"\rSyncing: 25%{Environment.NewLine}Almost there{Environment.NewLine}");
	}

	[TestMethod]
	[Description("Verifies a progress line opened while the command rendered JSON is closed on standard error once the command is over, not on the stream the next feedback uses.")]
	public async Task When_MachineReadableScopeEndsWithAnOpenLine_Then_TheLineIsClosedWhereItWasOpened()
	{
		var presenter = CreatePresenter(ReplFeedbackStream.Auto);
		using var output = new StringWriter();
		using var error = new StringWriter();
		using var session = OpenSession(output, error);

		using (ReplSessionIO.PushMachineReadableOutput(isMachineReadableOutput: true))
		{
			await presenter.PresentAsync(new ReplProgressEvent("Syncing", Percent: 25), CancellationToken.None);
		}

		await presenter.PresentAsync(new ReplStatusEvent("Done"), CancellationToken.None);

		error.ToString().Should().Be($"\rSyncing: 25%{Environment.NewLine}");
		output.ToString().Should().Be($"Done{Environment.NewLine}");
	}

	[TestMethod]
	[Description("Verifies a progress line on a new stream is padded for that stream alone, not to the width of the line left on the previous one.")]
	public async Task When_ProgressMovesToAnotherStream_Then_ItIsNotPaddedToThePreviousLine()
	{
		var presenter = CreatePresenter(ReplFeedbackStream.Auto);
		using var output = new StringWriter();
		using var error = new StringWriter();
		using var session = OpenSession(output, error);

		using (ReplSessionIO.PushMachineReadableOutput(isMachineReadableOutput: true))
		{
			await presenter.PresentAsync(new ReplProgressEvent("Synchronizing", Percent: 25), CancellationToken.None);
		}

		await presenter.PresentAsync(new ReplProgressEvent("Go", Percent: 5), CancellationToken.None);

		output.ToString().Should().Be("\rGo: 5%");
	}

	[TestMethod]
	[Description("Verifies the presenter, shared by every session of an app, never writes into another session's stream to close a progress line that session left open.")]
	public async Task When_AnotherSessionLeftAProgressLineOpen_Then_ItsWriterIsNotTouched()
	{
		var presenter = CreatePresenter(ReplFeedbackStream.Auto);
		using var first = new StringWriter();
		using var second = new StringWriter();

		using (OpenSession(first, first))
		{
			await presenter.PresentAsync(new ReplProgressEvent("Syncing", Percent: 25), CancellationToken.None);
		}

		using (OpenSession(second, second))
		{
			await presenter.PresentAsync(new ReplStatusEvent("Hello"), CancellationToken.None);
		}

		first.ToString().Should().Be("\rSyncing: 25%");
		second.ToString().Should().Be($"Hello{Environment.NewLine}");
	}

	[TestMethod]
	[Description("Verifies sessions that interleave progress keep their own open line: a session's next feedback still starts on a new line after another session wrote progress in between.")]
	public async Task When_SessionsInterleaveProgress_Then_EachClosesItsOwnLine()
	{
		var presenter = CreatePresenter(ReplFeedbackStream.Auto);
		using var first = new StringWriter();
		using var second = new StringWriter();

		using (OpenSession(first, first))
		{
			await presenter.PresentAsync(new ReplProgressEvent("Syncing", Percent: 25), CancellationToken.None);
		}

		using (OpenSession(second, second))
		{
			await presenter.PresentAsync(new ReplProgressEvent("Downloading", Percent: 50), CancellationToken.None);
		}

		using (OpenSession(first, first))
		{
			await presenter.PresentAsync(new ReplStatusEvent("Done A"), CancellationToken.None);
		}

		first.ToString().Should().Be($"\rSyncing: 25%{Environment.NewLine}Done A{Environment.NewLine}");
		second.ToString().Should().Be("\rDownloading: 50%");
	}

	[TestMethod]
	[Description("Verifies feedback moved to a terminal standard error is coloured and carries the terminal progress sequence while standard output is piped: both follow the stream feedback is written to.")]
	public async Task When_FeedbackGoesToATerminalErrorStreamWhileOutputIsPiped_Then_ItIsColouredWithTerminalProgress()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		using var error = new StringWriter();
		var presenter = new ConsoleReplInteractionPresenter(
			new InteractionOptions { AdvancedProgressMode = AdvancedProgressMode.Always },
			new OutputOptions { AnsiMode = AnsiMode.Auto },
			_ => new FeedbackTarget(error, IsError: true, IsSessionWriter: false, IsStreamRedirected: false));

		await presenter.PresentAsync(new ReplWarningEvent("Cache is warming up"), CancellationToken.None);
		await presenter.PresentAsync(new ReplProgressEvent("Syncing", Percent: 25), CancellationToken.None);

		error.ToString().Should().Contain("\u001b[", "the warning is coloured");
		error.ToString().Should().Contain("\u001b]9;4;1;25\u0007", "the terminal progress sequence is emitted");
	}

	private static ConsoleReplInteractionPresenter CreatePresenter(ReplFeedbackStream stream) =>
		new(new InteractionOptions(), new OutputOptions { AnsiMode = AnsiMode.Never, FeedbackStream = stream });

	private static IDisposable OpenSession(TextWriter output, TextWriter error) =>
		ReplSessionIO.SetSession(output, TextReader.Null, AnsiMode.Never, error: error, isHostedSession: false);
}
