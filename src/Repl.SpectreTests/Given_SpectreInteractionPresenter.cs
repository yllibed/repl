using System.Text;
using Repl.Interaction;

namespace Repl.SpectreTests;

[TestClass]
public sealed class Given_SpectreInteractionPresenter
{
	[TestMethod]
	[Description("Regression guard: verifies TextWriter capture emits plain text without ANSI or OSC control sequences.")]
	public async Task When_CapturingToTextWriter_Then_OutputRemainsPlainText()
	{
		var writer = new StringWriter(new StringBuilder());
		var presenter = new SpectreInteractionPresenter(new RecordingPresenter());

		using (presenter.BeginCapture(writer))
		{
			await presenter.PresentAsync(new ReplProgressEvent("Downloading", Percent: 42.5), CancellationToken.None);
			await presenter.PresentAsync(new ReplProblemEvent("Boom", "Something happened", "oops"), CancellationToken.None);
			await presenter.PresentAsync(new ReplClearScreenEvent(), CancellationToken.None);
		}

		var text = writer.ToString();
		text.Should().Contain("Progress: Downloading: 42.5%");
		text.Should().Contain("Problem [oops]: Boom");
		text.Should().Contain("Something happened");
		text.Should().NotContain("\u001b[");
		text.Should().NotContain("]9;4;");
	}

	[TestMethod]
	[Description("Regression guard: verifies nested capture scopes restore the previous sink when the inner scope completes.")]
	public async Task When_CaptureScopesAreNested_Then_PreviousSinkIsRestored()
	{
		var fallback = new RecordingPresenter();
		var outer = new RecordingPresenter();
		var inner = new RecordingPresenter();
		var presenter = new SpectreInteractionPresenter(fallback);

		using (presenter.BeginCapture(outer))
		{
			await presenter.PresentAsync(new ReplStatusEvent("outer-1"), CancellationToken.None);

			using (presenter.BeginCapture(inner))
			{
				await presenter.PresentAsync(new ReplStatusEvent("inner"), CancellationToken.None);
			}

			await presenter.PresentAsync(new ReplStatusEvent("outer-2"), CancellationToken.None);
		}

		await presenter.PresentAsync(new ReplStatusEvent("fallback"), CancellationToken.None);

		outer.Events.Should().ContainInOrder("outer-1", "outer-2");
		inner.Events.Should().ContainSingle().Which.Should().Be("inner");
		fallback.Events.Should().ContainSingle().Which.Should().Be("fallback");
	}

	private sealed class RecordingPresenter : IReplInteractionPresenter
	{
		public List<string> Events { get; } = [];

		public ValueTask PresentAsync(ReplInteractionEvent evt, CancellationToken cancellationToken)
		{
			Events.Add(evt switch
			{
				ReplStatusEvent status => status.Text,
				ReplNoticeEvent notice => notice.Text,
				ReplWarningEvent warning => warning.Text,
				ReplPromptEvent prompt => prompt.PromptText,
				ReplProblemEvent problem => problem.Summary,
				ReplProgressEvent progress => progress.Label,
				ReplClearScreenEvent => "clear",
				_ => evt.GetType().Name,
			});
			return ValueTask.CompletedTask;
		}
	}
}
