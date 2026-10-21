using Repl.Tests.TerminalSupport;

namespace Repl.Tests;

[TestClass]
public sealed class Given_RemoteSecretPrompt
{
	[TestMethod]
	[Description("Verifies a secret typed in a session with a key reader, as hosted terminals have, is echoed as its mask, or not at all without one, never in clear text, with or without a timeout.")]
	[DataRow('*', false, DisplayName = "Default mask")]
	[DataRow('#', true, DisplayName = "Custom mask, with a timeout")]
	[DataRow(null, false, DisplayName = "No mask")]
	public async Task When_ASecretIsTypedThroughAKeyReader_Then_ItIsNeverEchoedInClear(char? mask, bool withTimeout)
	{
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions());
		try
		{
			// None of the typed characters appears in the prompt's label, so any of them in the output is an echo.
			ReplSessionIO.KeyReader = new FakeKeyReader(
			[
				Key(ConsoleKey.Q, 'q'),
				Key(ConsoleKey.Z, 'z'),
				Key(ConsoleKey.X, 'x'),
				Key(ConsoleKey.Backspace, '\b'),
				Key(ConsoleKey.D7, '7'),
				Key(ConsoleKey.Enter, '\r'),
			]);

			var secret = await channel.AskSecretAsync(
				"pin",
				"Pin",
				new AskSecretOptions(Timeout: withTimeout ? TimeSpan.FromMinutes(1) : null, Mask: mask));

			secret.Should().Be("qz7");
			var echoed = output.ToString();
			echoed.Should().NotContainAny(["q", "z", "x", "7"], "no typed character may be echoed");
			// Three masks, Backspace erasing one, a fourth, then the carriage return a remote terminal needs.
			var expected = mask is { } shown
				? $"Pin: {new string(shown, 3)}\b \b{shown}\r\n"
				: "Pin: \r\n";
			echoed.Should().EndWith(expected);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Verifies Esc cancels a secret typed through a key reader, erasing its mask, without echoing what was typed, with or without a timeout.")]
	[DataRow(false, DisplayName = "Without a timeout")]
	[DataRow(true, DisplayName = "With a timeout")]
	public async Task When_EscIsPressedAtASecretPrompt_Then_ItIsCancelledWithoutEchoingTheSecret(bool withTimeout)
	{
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions());
		try
		{
			ReplSessionIO.KeyReader = new FakeKeyReader(
			[
				Key(ConsoleKey.Q, 'q'),
				Key(ConsoleKey.Z, 'z'),
				Key(ConsoleKey.Escape, '\u001b'),
			]);

			var act = async () => await channel.AskSecretAsync(
					"pin", "Pin", new AskSecretOptions(Timeout: withTimeout ? TimeSpan.FromMinutes(1) : null))
				.ConfigureAwait(false);

			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
			output.ToString().Should().NotContainAny(["q", "z"]).And.Contain("**\b\b  \b\b");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Verifies a secret prompt whose terminal disconnects while the user types fails instead of returning an empty answer to the command, with or without a timeout.")]
	[DataRow(false, DisplayName = "Without a timeout")]
	[DataRow(true, DisplayName = "With a timeout")]
	public async Task When_TheTerminalDisconnectsAtASecretPrompt_Then_ThePromptFails(bool withTimeout)
	{
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions());
		try
		{
			ReplSessionIO.KeyReader = new DisconnectedKeyReader();

			var act = async () => await channel.AskSecretAsync(
					"pin", "Pin", new AskSecretOptions(Timeout: withTimeout ? TimeSpan.FromMinutes(1) : null, AllowEmpty: true))
				.ConfigureAwait(false);

			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	// A transport whose stream ended, as VtKeyReader reports it: a cancellation the caller did not request.
	private sealed class DisconnectedKeyReader : IReplKeyReader
	{
		public bool KeyAvailable => false;

		public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct) =>
			ValueTask.FromException<ConsoleKeyInfo>(new OperationCanceledException(ct));
	}

	private static ConsoleKeyInfo Key(ConsoleKey key, char character) =>
		new(character, key, shift: false, alt: false, control: false);
}
