using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Repl.Tests;

[TestClass]
public sealed class Given_PromptTimeProvider
{
	[TestMethod]
	[Description("Channel accepts a custom TimeProvider at construction time.")]
	public void When_FakeTimeProviderInjected_Then_ChannelIsCreated()
	{
		var fakeTime = new FakeTimeProvider();

		var channel = new ConsoleInteractionChannel(
			new InteractionOptions(),
			timeProvider: fakeTime);

		channel.Should().NotBeNull();
	}

	[TestMethod]
	[Description("Prompt timeout fires based on the injected TimeProvider, not wall-clock time.")]
	public async Task When_FakeTimeAdvanced_Then_TimeoutFiresWithoutWallClockWait()
	{
		var fakeTime = new FakeTimeProvider();
		var channel = new ConsoleInteractionChannel(
			new InteractionOptions { PromptFallback = PromptFallback.UseDefault },
			timeProvider: fakeTime);

		// Use a TextReader that blocks until cancelled — simulates a user who never types.
		using var blockingReader = new BlockingTextReader();
		var original = Console.In;
		Console.SetIn(blockingReader);
		try
		{
			var choiceTask = channel.AskChoiceAsync(
				"color",
				"Pick a color?",
				["Red", "Green", "Blue"],
				defaultIndex: 1,
				new AskOptions(Timeout: TimeSpan.FromSeconds(10)));

			// No wall-clock time passes — advance the fake clock.
			fakeTime.Advance(TimeSpan.FromSeconds(11));

			// The timeout should fire almost instantly because FakeTimeProvider controls time.
			var index = await choiceTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

			index.Should().Be(1, "timeout should auto-select the default index");
		}
		finally
		{
			Console.SetIn(original);
		}
	}

	/// <summary>
	/// A TextReader that blocks on ReadLineAsync until the read is cancelled externally
	/// (via the CancellationToken propagated through the prompt timeout).
	/// </summary>
	private sealed class BlockingTextReader : TextReader
	{
		private readonly TaskCompletionSource<string?> _tcs = new();

		public override string? ReadLine() => null;


#pragma warning disable VSTHRD003 // Test helper — intentionally returns externally-controlled task.
		public override Task<string?> ReadLineAsync()
			=> _tcs.Task;
#pragma warning restore VSTHRD003

		protected override void Dispose(bool disposing)
		{
			_tcs.TrySetResult(null);
			base.Dispose(disposing);
		}
	}
}
