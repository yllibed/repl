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

	private sealed class StubTransformer : IOutputTransformer
	{
		public string Name => "stub";

		public ValueTask<string> TransformAsync(object? value, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(value?.ToString() ?? string.Empty);
	}
}
