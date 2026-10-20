using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
public sealed class Given_InteractionFeedbackRouting
{
	[TestMethod]
	[Description("Verifies built-in transformers declare whether their output is meant for programs.")]
	public void When_BuiltInTransformersAreRegistered_Then_OnlyStructuredFormatsAreMachineReadable()
	{
		var options = new OutputOptions();

		options.Transformers["json"].IsMachineReadable.Should().BeTrue();
		options.Transformers["xml"].IsMachineReadable.Should().BeTrue();
		options.Transformers["yaml"].IsMachineReadable.Should().BeTrue();
		options.Transformers["markdown"].IsMachineReadable.Should().BeFalse();
		options.Transformers["human"].IsMachineReadable.Should().BeFalse();
	}

	[TestMethod]
	[Description("Verifies a custom transformer is treated as human output unless it opts in.")]
	public void When_CustomTransformerDoesNotDeclareIt_Then_ItIsNotMachineReadable()
	{
		IOutputTransformer transformer = new StubTransformer();

		transformer.IsMachineReadable.Should().BeFalse();
	}

	[TestMethod]
	[Description("Verifies feedback stays on standard output by default only when it cannot reach a program.")]
	[DataRow(false, false, false, DisplayName = "Human output on a terminal")]
	[DataRow(true, false, true, DisplayName = "Machine output on a terminal")]
	[DataRow(false, true, true, DisplayName = "Human output, local standard output redirected")]
	[DataRow(true, true, true, DisplayName = "Machine output, local standard output redirected")]
	public void When_StreamIsAuto_Then_FeedbackMovesToErrorForPrograms(
		bool isMachineReadableOutput,
		bool isLocalOutputRedirected,
		bool expectedToError)
	{
		var toError = InteractionFeedbackRouting.WritesToError(
			ReplFeedbackStream.Auto,
			isMachineReadableOutput,
			isLocalOutputRedirected);

		toError.Should().Be(expectedToError);
	}

	[TestMethod]
	[Description("Verifies the process's redirected standard output does not move a session's feedback: the session's writers are not the process's.")]
	public void When_ASessionIsActive_Then_TheProcessRedirectionIsIgnored()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		using var session = ReplSessionIO.SetSession(output, TextReader.Null, error: error, isHostedSession: false);

		var target = InteractionFeedbackRouting.Resolve(new OutputOptions(), isOutputRedirected: true, isErrorRedirected: true);

		target.Writer.Should().BeSameAs(output);
		target.IsTerminal.Should().BeTrue();
	}

	[TestMethod]
	[Description("Verifies an explicit stream wins over every detection.")]
	[DataRow(ReplFeedbackStream.Output, false)]
	[DataRow(ReplFeedbackStream.Error, true)]
	public void When_StreamIsExplicit_Then_DetectionIsIgnored(ReplFeedbackStream stream, bool expectedToError)
	{
		foreach (var isMachineReadableOutput in new[] { false, true })
		{
			foreach (var isLocalOutputRedirected in new[] { false, true })
			{
				InteractionFeedbackRouting.WritesToError(stream, isMachineReadableOutput, isLocalOutputRedirected)
					.Should().Be(expectedToError);
			}
		}
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
	[Description("Verifies the target carries the redirection of the stream feedback is written to, not standard output's, for the colour and terminal-sequence rules to read.")]
	[DataRow(ReplFeedbackStream.Auto, true, false, false, DisplayName = "Piped stdout, terminal stderr: stderr receives it, not redirected")]
	[DataRow(ReplFeedbackStream.Error, false, true, true, DisplayName = "Terminal stdout, redirected stderr: stderr receives it, redirected")]
	[DataRow(ReplFeedbackStream.Output, true, false, true, DisplayName = "Forced stdout, piped: redirected")]
	public void When_ResolvingOutsideASession_Then_TheTargetCarriesItsOwnStreamRedirection(
		ReplFeedbackStream stream,
		bool isOutputRedirected,
		bool isErrorRedirected,
		bool expectedStreamRedirected)
	{
		var target = InteractionFeedbackRouting.Resolve(
			new OutputOptions { FeedbackStream = stream },
			isOutputRedirected,
			isErrorRedirected);

		target.IsStreamRedirected.Should().Be(expectedStreamRedirected);
	}

	[TestMethod]
	[Description("Verifies the colour rule of the output stream applies to whichever stream it is given: a terminal one is coloured, a redirected one is not.")]
	public void When_AskingForColourOnAGivenStream_Then_ItsRedirectionDecides()
	{
		using var env = new EnvironmentVariableScope(TerminalTestEnvironments.Neutral);
		var options = new OutputOptions { AnsiMode = AnsiMode.Auto };

		options.IsAnsiEnabled(isStreamRedirected: false).Should().BeTrue();
		options.IsAnsiEnabled(isStreamRedirected: true).Should().BeFalse();
		new OutputOptions { AnsiMode = AnsiMode.Always }.IsAnsiEnabled(isStreamRedirected: true)
			.Should().BeTrue("forced colour applies to a redirected stream too, as it does to standard output");
	}

	private sealed class StubTransformer : IOutputTransformer
	{
		public string Name => "stub";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(value?.ToString() ?? string.Empty);
	}
}
