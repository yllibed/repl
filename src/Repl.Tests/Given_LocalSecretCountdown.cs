using Microsoft.Extensions.Time.Testing;

namespace Repl.Tests;

[TestClass]
public sealed class Given_LocalSecretCountdown
{
	[TestMethod]
	[Description("Verifies Esc cancels a local secret prompt with a timeout, before the deadline or after it once typing has kept the prompt open: it is never read as a timeout.")]
	[DataRow(false, DisplayName = "Before the deadline")]
	[DataRow(true, DisplayName = "After the deadline, while typing")]
	public async Task When_EscIsPressedWhileTyping_Then_ThePromptIsCancelled(bool pastDeadline)
	{
		var time = new FakeTimeProvider();
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions(), timeProvider: time);
		var keys = new TypeThenEscKeys();
		// Cancelled once the test is over, so a regressed read never keeps holding the shared console input gate.
		using var cleanup = new CancellationTokenSource();
		try
		{
			var read = channel.ReadSecretWithCountdownAsync(
					TimeSpan.FromSeconds(5), '*', TextWriter.Null, keys.Source, cleanup.Token)
				.AsTask();
			await keys.WaitUntilTypedAsync().ConfigureAwait(false);
			if (pastDeadline)
			{
				time.Advance(TimeSpan.FromSeconds(6));
			}

			keys.PressEsc();

			var act = () => read.WaitAsync(TimeSpan.FromSeconds(10));
			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		}
		finally
		{
			await cleanup.CancelAsync().ConfigureAwait(false);
		}
	}

	[TestMethod]
	[Description("Verifies a local secret prompt with a timeout returns no answer once the deadline passes with nothing typed.")]
	public async Task When_TheDeadlinePassesWithNothingTyped_Then_ThePromptReturnsNoAnswer()
	{
		var time = new FakeTimeProvider();
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions(), timeProvider: time);
		var noKeys = new LocalKeySource(
			static () => false,
			static () => throw new InvalidOperationException("No key is ever available."));
		using var cleanup = new CancellationTokenSource();
		try
		{
			var read = channel.ReadSecretWithCountdownAsync(
					TimeSpan.FromSeconds(5), '*', TextWriter.Null, noKeys, cleanup.Token)
				.AsTask();
			time.Advance(TimeSpan.FromSeconds(6));

			var secret = await read.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
			secret.Should().BeNull();
		}
		finally
		{
			await cleanup.CancelAsync().ConfigureAwait(false);
		}
	}

	// Types one character, then holds Esc back until the test presses it.
	private sealed class TypeThenEscKeys
	{
		private readonly TaskCompletionSource _typed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _reads;
		private bool _escPressed;

		public LocalKeySource Source => new(KeyAvailable, ReadKey);

		public Task WaitUntilTypedAsync() => _typed.Task.WaitAsync(TimeSpan.FromSeconds(10));

		public void PressEsc() => Volatile.Write(ref _escPressed, value: true);

		private bool KeyAvailable() => Volatile.Read(ref _reads) == 0 || Volatile.Read(ref _escPressed);

		private ConsoleKeyInfo ReadKey()
		{
			if (Interlocked.Increment(ref _reads) == 1)
			{
				_typed.SetResult();
				return new ConsoleKeyInfo('x', ConsoleKey.X, shift: false, alt: false, control: false);
			}

			return new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, shift: false, alt: false, control: false);
		}
	}
}
