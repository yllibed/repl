namespace Repl.Tests;

[TestClass]
public sealed class Given_HostedPromptDisconnect
{
	[TestMethod]
	[Description("Verifies a prompt whose hosted terminal disconnects before it is answered fails, instead of answering its default on behalf of a user who is no longer there, with or without a timeout.")]
	[DataRow("text", false)]
	[DataRow("text", true)]
	[DataRow("confirmation", false)]
	[DataRow("confirmation", true)]
	[DataRow("choice", false)]
	[DataRow("multi-choice", false)]
	public async Task When_TheTerminalDisconnectsAtAPrompt_Then_ThePromptFails(string kind, bool withTimeout)
	{
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions());
		var options = new AskOptions(Timeout: withTimeout ? TimeSpan.FromMinutes(1) : null);
		try
		{
			ReplSessionIO.KeyReader = new DisconnectedKeyReader();

			var act = kind switch
			{
				"text" => async () => await channel.AskTextAsync("name", "Name", "fallback", options).ConfigureAwait(false),
				"confirmation" => async () => await channel.AskConfirmationAsync("go", "Proceed?", defaultValue: true, options).ConfigureAwait(false),
				"choice" => async () => await channel.AskChoiceAsync("pick", "Pick", ["one", "two"], defaultIndex: 1, options).ConfigureAwait(false),
				_ => (Func<Task>)(async () => await channel.AskMultiChoiceAsync("picks", "Picks", ["one", "two"], [0]).ConfigureAwait(false)),
			};

			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Verifies a prompt that must be answered, whose hosted terminal disconnects, is cancelled like any other rather than failing as unanswered.")]
	public async Task When_TheTerminalDisconnectsAtAPromptThatMustBeAnswered_Then_ThePromptIsCancelled()
	{
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions { PromptFallback = PromptFallback.Fail }, new OutputOptions());
		try
		{
			ReplSessionIO.KeyReader = new DisconnectedKeyReader();

			var act = async () => await channel.AskTextAsync("name", "Name").ConfigureAwait(false);

			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Verifies a prompt whose input is redirected and ends still answers its default, as scripted input relies on.")]
	public async Task When_RedirectedInputEndsAtAPrompt_Then_ItAnswersItsDefault()
	{
		using var output = new StringWriter();
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions());

		var answer = await channel.AskTextAsync("name", "Name", "fallback").ConfigureAwait(false);

		answer.Should().Be("fallback");
	}

	// A hosted terminal whose stream ended: its reader stops without its token being cancelled.
	private sealed class DisconnectedKeyReader : IReplKeyReader
	{
		public bool KeyAvailable => false;

		public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct) =>
			ValueTask.FromException<ConsoleKeyInfo>(new OperationCanceledException(ct));
	}
}
