namespace Repl.Tests;

[TestClass]
public sealed class Given_InteractiveCallerCancellation
{
	[TestMethod]
	[Description("Verifies the caller cancelling while the line editor waits for a key propagates the caller's cancellation by default, instead of being read as the end of input and a success.")]
	public async Task When_TheCallerCancelsWhileTheLineEditorWaitsForAKey_Then_TheCancellationPropagates()
	{
		var app = CreateApp();
		using var caller = new CancellationTokenSource();

		var act = () => CancelWhileWaitingForAKeyAsync(app, caller);

		(await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false))
			.Which.CancellationToken.Should().Be(caller.Token);
	}

	[TestMethod]
	[Description("Verifies the caller cancelling while the line editor waits for a key returns the configured cancellation exit code, not the success code of an ended input.")]
	public async Task When_TheCallerCancelsWhileTheLineEditorWaitsForAKey_Then_TheConfiguredCancellationCodeIsReturned()
	{
		var app = CreateApp();
		app.Options(options => options.ExitCodes.Cancelled = 73);
		using var caller = new CancellationTokenSource();

		var exitCode = await CancelWhileWaitingForAKeyAsync(app, caller).ConfigureAwait(false);

		exitCode.Should().Be(73);
	}

	[TestMethod]
	[Description("Verifies the caller cancelling while the line editor waits for a key reaches the exit code resolver as a cancellation, not as a success.")]
	public async Task When_TheCallerCancelsWhileTheLineEditorWaitsForAKey_Then_TheResolverSeesACancellation()
	{
		var app = CreateApp();
		ReplExecutionOutcomeKind? resolved = null;
		app.Options(options => options.ExitCodes.Resolver = outcome =>
		{
			resolved = outcome.Kind;
			return outcome.ExitCode;
		});
		using var caller = new CancellationTokenSource();

		var exitCode = await CancelWhileWaitingForAKeyAsync(app, caller).ConfigureAwait(false);

		exitCode.Should().Be(130);
		resolved.Should().Be(ReplExecutionOutcomeKind.Cancelled);
	}

	[TestMethod]
	[Description("Verifies a key reader whose input ends without the caller cancelling, as VtKeyReader reports the end of its stream, still ends the interactive session as the end of input, successfully.")]
	public async Task When_TheKeyReaderInputEnds_Then_TheSessionEndsSuccessfully()
	{
		var app = CreateApp();
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		try
		{
			ReplSessionIO.KeyReader = new EndedKeyReader();

			var exitCode = await app.RunAsync([]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

			exitCode.Should().Be(0, output.ToString());
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Verifies a prompt with a timeout in a hosted session still answers its default when the deadline expires while it waits for a key.")]
	public async Task When_AHostedPromptTimesOut_Then_ItAnswersItsDefault()
	{
		var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions(), timeProvider: time);
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var keys = new WaitingKeyReader();
		try
		{
			ReplSessionIO.KeyReader = keys;
			var ask = channel.AskTextAsync("name", "Name", "fallback", new AskOptions(Timeout: TimeSpan.FromSeconds(5))).AsTask();
			await WaitUntilWaitingAsync(keys, ask).ConfigureAwait(false);

			time.Advance(TimeSpan.FromSeconds(10));

			(await ask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)).Should().Be("fallback");
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	[TestMethod]
	[Description("Verifies the caller cancelling a prompt in a hosted session while it waits for a key cancels it, instead of answering its default as if the input had ended.")]
	public async Task When_AHostedPromptIsCancelledByTheCaller_Then_TheCancellationPropagates()
	{
		var channel = new ConsoleInteractionChannel(new InteractionOptions(), new OutputOptions());
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var keys = new WaitingKeyReader();
		using var caller = new CancellationTokenSource();
		try
		{
			ReplSessionIO.KeyReader = keys;
			var ask = channel.AskTextAsync("name", "Name", "fallback", new AskOptions(CancellationToken: caller.Token)).AsTask();
			await WaitUntilWaitingAsync(keys, ask).ConfigureAwait(false);

			await caller.CancelAsync().ConfigureAwait(false);

			var act = () => ask.WaitAsync(TimeSpan.FromSeconds(10));
			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	private static CoreReplApp CreateApp()
	{
		var app = CoreReplApp.Create();
		app.Map("ping", () => "pong");
		return app;
	}

	// Runs an interactive session whose line editor waits for a key, then cancels the caller once it waits.
	private static async Task<int> CancelWhileWaitingForAKeyAsync(CoreReplApp app, CancellationTokenSource caller)
	{
		using var output = new StringWriter();
		var previousReader = ReplSessionIO.KeyReader;
		using var session = ReplSessionIO.SetSession(output, TextReader.Null);
		var keys = new WaitingKeyReader();
		try
		{
			ReplSessionIO.KeyReader = keys;
			var run = app.RunAsync([], caller.Token).AsTask();
			await WaitUntilWaitingAsync(keys, run).ConfigureAwait(false);

			await caller.CancelAsync().ConfigureAwait(false);

			return await run.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
		}
		finally
		{
			ReplSessionIO.KeyReader = previousReader;
		}
	}

	// Waits until the reader waits for a key, surfacing the operation's own failure if it ends first.
	private static async Task WaitUntilWaitingAsync(WaitingKeyReader keys, Task operation)
	{
		var first = await Task.WhenAny(keys.Waiting, operation).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
		if (first == operation)
		{
			Assert.Fail($"The operation ended ({operation.Status}) before it waited for a key: {operation.Exception?.GetBaseException()}");
		}
	}

	// Waits for a key that never comes, signalling once it waits, until its token is cancelled.
	private sealed class WaitingKeyReader : IReplKeyReader
	{
		private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task Waiting => _waiting.Task;

		public bool KeyAvailable => false;

		public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct)
		{
			_waiting.TrySetResult();
			await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
			return default;
		}
	}

	// A key reader whose input has ended: it stops without its token being cancelled.
	private sealed class EndedKeyReader : IReplKeyReader
	{
		public bool KeyAvailable => false;

		public ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct) =>
			ValueTask.FromException<ConsoleKeyInfo>(new OperationCanceledException(ct));
	}
}
