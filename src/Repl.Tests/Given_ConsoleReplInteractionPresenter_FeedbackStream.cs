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
	[Description("Verifies the routing reads the stream it actually writes to: feedback moved to a redirected standard error is not a terminal even when standard output is one.")]
	[DataRow(ReplFeedbackStream.Auto, true, false, true, true, DisplayName = "Redirected stdout, terminal stderr: error stream, a terminal")]
	[DataRow(ReplFeedbackStream.Error, false, true, true, false, DisplayName = "Terminal stdout, redirected stderr: error stream, not a terminal")]
	[DataRow(ReplFeedbackStream.Output, false, true, false, true, DisplayName = "Forced output on a terminal stdout")]
	[DataRow(ReplFeedbackStream.Output, true, false, false, false, DisplayName = "Forced output on a redirected stdout")]
	public void When_ResolvingOutsideASession_Then_TheTargetReflectsItsOwnStream(
		ReplFeedbackStream stream,
		bool isOutputRedirected,
		bool isErrorRedirected,
		bool expectedIsError,
		bool expectedIsTerminal)
	{
		var target = InteractionFeedbackRouting.Resolve(
			new OutputOptions { FeedbackStream = stream },
			isOutputRedirected,
			isErrorRedirected);

		target.IsError.Should().Be(expectedIsError);
		target.IsTerminal.Should().Be(expectedIsTerminal);
		target.Writer.Should().BeSameAs(expectedIsError ? Console.Error : Console.Out);
	}

	[TestMethod]
	[Description("Verifies colour follows the stream it is written to: never into a redirected standard error, even when standard output's own colour rule allows it.")]
	[DataRow(true, false, true, true, DisplayName = "Output on a terminal")]
	[DataRow(true, true, true, true, DisplayName = "Error on a terminal")]
	[DataRow(true, true, false, false, DisplayName = "Error redirected")]
	[DataRow(false, true, true, false, DisplayName = "Colour disabled for the output")]
	[DataRow(true, false, false, true, DisplayName = "Output redirected but coloured by its own rule (CLICOLOR_FORCE)")]
	public void When_StylingFeedback_Then_ColourFollowsTheTargetStream(
		bool ansiEnabledForOutput,
		bool isError,
		bool isTerminal,
		bool expected)
	{
		new FeedbackTarget(TextWriter.Null, IsError: isError, IsTerminal: isTerminal)
			.CanStyle(ansiEnabledForOutput)
			.Should().Be(expected);
	}

	private static ConsoleReplInteractionPresenter CreatePresenter(ReplFeedbackStream stream) =>
		new(new InteractionOptions(), new OutputOptions { AnsiMode = AnsiMode.Never, FeedbackStream = stream });

	private static IDisposable OpenSession(TextWriter output, TextWriter error) =>
		ReplSessionIO.SetSession(output, TextReader.Null, AnsiMode.Never, error: error, isHostedSession: false);
}
