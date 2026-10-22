using Microsoft.Extensions.Time.Testing;
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

	[TestMethod]
	[Description("Verifies Esc pressed before the deadline cancels a timed prompt in a hosted terminal even when the deadline expires while Esc's erase is being echoed: the prompt never reads it as a timeout.")]
	[DataRow(true, DisplayName = "Secret prompt")]
	[DataRow(false, DisplayName = "Text prompt")]
	public async Task When_TheDeadlineExpiresWhileEscIsEchoed_Then_ThePromptIsStillCancelled(bool secret)
	{
		var time = new FakeTimeProvider();
		using var output = new DeadlineDuringEchoWriter(time, marker: "\b");
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions(), timeProvider: time);
		try
		{
			ReplSessionIO.KeyReader = new FakeKeyReader([Key(ConsoleKey.Q, 'q'), Key(ConsoleKey.Escape, '\u001b')]);

			var act = () => AskTimedAsync(channel, secret);

			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
			output.Fired.Should().BeTrue("the deadline must expire during the erase for the race to be exercised");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Verifies an answer submitted with Enter before the deadline is kept by a timed prompt in a hosted terminal even when the deadline expires while the new line is being echoed.")]
	[DataRow(true, DisplayName = "Secret prompt")]
	[DataRow(false, DisplayName = "Text prompt")]
	public async Task When_TheDeadlineExpiresWhileEnterIsEchoed_Then_TheAnswerIsKept(bool secret)
	{
		var time = new FakeTimeProvider();
		using var output = new DeadlineDuringEchoWriter(time, marker: "\r\n");
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions(), timeProvider: time);
		try
		{
			ReplSessionIO.KeyReader = new FakeKeyReader([Key(ConsoleKey.Q, 'q'), Key(ConsoleKey.Enter, '\r')]);

			var answer = await AskTimedAsync(channel, secret).ConfigureAwait(false);

			answer.Should().Be("q");
			output.Fired.Should().BeTrue("the deadline must expire during the new line for the race to be exercised");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Verifies a timed secret prompt in a hosted terminal stops honouring its deadline once typing has started, as the local console does, so the whole secret is read rather than its rest being left for the next, clear-text reader.")]
	public async Task When_TheDeadlineExpiresWhileASecretIsTyped_Then_TheWholeSecretIsRead()
	{
		var time = new FakeTimeProvider();
		using var output = new DeadlineDuringEchoWriter(time, marker: "*");
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions(), timeProvider: time);
		try
		{
			var keys = new FakeKeyReader([Key(ConsoleKey.Q, 'q'), Key(ConsoleKey.Z, 'z'), Key(ConsoleKey.Enter, '\r')]);
			ReplSessionIO.KeyReader = keys;

			var secret = await channel.AskSecretAsync(
					"pin", "Pin", new AskSecretOptions(Timeout: TimeSpan.FromSeconds(5), AllowEmpty: true))
				.ConfigureAwait(false);

			secret.Should().Be("qz");
			keys.KeyAvailable.Should().BeFalse("no part of the secret may be left for the next reader");
			output.Fired.Should().BeTrue("the deadline must expire during typing for the case to be exercised");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Verifies Esc read by the hosted terminal's key reader cancels a timed secret prompt even when the deadline expires while the reader waits to tell a lone Esc from an escape sequence.")]
	public async Task When_TheDeadlineExpiresWhileEscIsDisambiguated_Then_ThePromptIsStillCancelled()
	{
		var time = new FakeTimeProvider();
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions(), timeProvider: time);
		try
		{
			ReplSessionIO.KeyReader = new VtKeyReader(new EscThenDeadlineReader(time));

			var act = async () => await channel.AskSecretAsync(
					"pin", "Pin", new AskSecretOptions(Timeout: TimeSpan.FromSeconds(5), AllowEmpty: true))
				.ConfigureAwait(false);

			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Verifies a terminal report that is not a keystroke, such as a window resize, does not stop a timed secret prompt's deadline: with nothing typed, the prompt still times out.")]
	public async Task When_TheTerminalReportsAResizeBeforeTyping_Then_TheDeadlineStillApplies()
	{
		var time = new FakeTimeProvider();
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions(), timeProvider: time);
		try
		{
			ReplSessionIO.KeyReader = new VtKeyReader(new ResizeThenDeadlineReader(time, "\u001b[8;24;80t"));

			var secret = await channel.AskSecretAsync(
					"pin", "Pin", new AskSecretOptions(Timeout: TimeSpan.FromSeconds(5), AllowEmpty: true))
				.AsTask()
				.WaitAsync(TimeSpan.FromSeconds(10))
				.ConfigureAwait(false);

			secret.Should().BeEmpty();
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Verifies a third-party key reader that stops without its token being cancelled, after typing started and the deadline expired, fails the timed secret prompt instead of returning an empty answer as a timeout.")]
	public async Task When_AThirdPartyReaderStopsAfterTheDeadlineExpired_Then_ThePromptFails()
	{
		var time = new FakeTimeProvider();
		using var output = new DeadlineDuringEchoWriter(time, marker: "*");
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions(), timeProvider: time);
		try
		{
			ReplSessionIO.KeyReader = new OneKeyThenDisconnectedKeyReader(Key(ConsoleKey.Q, 'q'));

			var act = async () => await channel.AskSecretAsync(
					"pin", "Pin", new AskSecretOptions(Timeout: TimeSpan.FromSeconds(5), AllowEmpty: true))
				.ConfigureAwait(false);

			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
			output.Fired.Should().BeTrue("the deadline must expire before the reader stops for the case to be exercised");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	private static async Task<string> AskTimedAsync(ConsoleInteractionChannel channel, bool secret) =>
		secret
			? await channel.AskSecretAsync(
					"pin", "Pin", new AskSecretOptions(Timeout: TimeSpan.FromSeconds(5), AllowEmpty: true))
				.ConfigureAwait(false)
			: await channel.AskTextAsync(
					"name", "Name", "fallback", new AskOptions(Timeout: TimeSpan.FromSeconds(5)))
				.ConfigureAwait(false);

	[TestMethod]
	[Description("Verifies a hosted terminal whose stream ends as the deadline expires fails the timed secret prompt instead of returning an empty answer as if it had timed out.")]
	public async Task When_TheTerminalStreamEndsAsTheDeadlineExpires_Then_TheSecretPromptFails()
	{
		var time = new FakeTimeProvider();
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions(), timeProvider: time);
		try
		{
			ReplSessionIO.KeyReader = new VtKeyReader(new EndsAtDeadlineReader(time));

			var act = async () => await channel.AskSecretAsync(
					"pin", "Pin", new AskSecretOptions(Timeout: TimeSpan.FromSeconds(5), AllowEmpty: true))
				.ConfigureAwait(false);

			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	// Expires the prompt's deadline while an echo containing the marker is being written, then lets the write complete.
	private sealed class DeadlineDuringEchoWriter(FakeTimeProvider time, string marker) : StringWriter
	{
		public bool Fired { get; private set; }

		public override void Write(string? value)
		{
			FireOnMarker(value);
			base.Write(value);
		}

		public override void Write(System.Text.StringBuilder? value)
		{
			FireOnMarker(value?.ToString());
			base.Write(value);
		}

		public override Task WriteAsync(string? value)
		{
			FireOnMarker(value);
			return base.WriteAsync(value);
		}

		private void FireOnMarker(string? value)
		{
			if (!Fired && value is not null && value.Contains(marker, StringComparison.Ordinal))
			{
				Fired = true;
				time.Advance(TimeSpan.FromSeconds(10));
			}
		}
	}

	// A terminal stream that ends at the moment the deadline expires: the clock moves first, then the read returns no data.
	private sealed class EndsAtDeadlineReader(FakeTimeProvider time) : TextReader
	{
		public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
		{
			time.Advance(TimeSpan.FromSeconds(10));
			return ValueTask.FromResult(0);
		}
	}

	// Sends Esc, then lets the deadline expire while the key reader waits for a possible escape sequence.
	private sealed class EscThenDeadlineReader(FakeTimeProvider time) : TextReader
	{
		private bool _sentEscape;

		public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
		{
			if (!_sentEscape)
			{
				_sentEscape = true;
				buffer.Span[0] = '\u001b';
				return ValueTask.FromResult(1);
			}

			time.Advance(TimeSpan.FromSeconds(10));
			return ValueTask.FromCanceled<int>(cancellationToken);
		}
	}

	// Sends a terminal report, then lets the deadline expire and waits until the read is cancelled.
	private sealed class ResizeThenDeadlineReader(FakeTimeProvider time, string report) : TextReader
	{
		private int _next;

		public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
		{
			if (_next < report.Length)
			{
				buffer.Span[0] = report[_next++];
				return ValueTask.FromResult(1);
			}

			time.Advance(TimeSpan.FromSeconds(10));
			return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
		}

		private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
		{
			await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
			return 0;
		}
	}

	// A third-party key reader that yields one key, then reports its stream ended as a plain cancellation.
	private sealed class OneKeyThenDisconnectedKeyReader(ConsoleKeyInfo key) : IReplKeyReader
	{
		private bool _sent;

		public bool KeyAvailable => !_sent;

		public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct)
		{
			if (_sent)
			{
				return ValueTask.FromException<ConsoleKeyInfo>(new OperationCanceledException(ct));
			}

			_sent = true;
			return ValueTask.FromResult(key);
		}
	}

	// A third-party key reader whose stream ended: a plain cancellation the caller did not request.
	private sealed class DisconnectedKeyReader : IReplKeyReader
	{
		public bool KeyAvailable => false;

		public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct) =>
			ValueTask.FromException<ConsoleKeyInfo>(new OperationCanceledException(ct));
	}

	private static ConsoleKeyInfo Key(ConsoleKey key, char character) =>
		new(character, key, shift: false, alt: false, control: false);
}
