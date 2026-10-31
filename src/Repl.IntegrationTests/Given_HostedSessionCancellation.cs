namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_HostedSessionCancellation
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	[TestMethod]
	[Description("Verifies the caller cancelling a hosted session while it detects the terminal returns the configured cancellation exit code, as a cancellation at any later point does.")]
	public async Task When_TheCallerCancelsWhileTheTerminalIsDetected_Then_TheConfiguredCancellationCodeIsReturned()
	{
		var app = CreateApp();
		app.Options(options => options.ExitCodes.Cancelled = 73);
		using var caller = new CancellationTokenSource();

		var exitCode = await CancelWhileDetectingAsync(app, caller).ConfigureAwait(false);

		exitCode.Should().Be(73);
	}

	[TestMethod]
	[Description("Verifies the caller cancelling a hosted session while it detects the terminal propagates the cancellation when no cancellation policy is configured.")]
	public async Task When_TheCallerCancelsWhileTheTerminalIsDetected_Then_TheCancellationPropagatesByDefault()
	{
		using var caller = new CancellationTokenSource();

		var act = () => CancelWhileDetectingAsync(CreateApp(), caller);

		(await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false))
			.Which.CancellationToken.Should().Be(caller.Token);
	}

	private static ReplApp CreateApp()
	{
		var app = ReplApp.Create();
		app.Map("ping", () => "pong");
		return app;
	}

	// Runs a hosted session whose terminal never reports its size, then cancels the caller once it waits for it.
	private static async Task<int> CancelWhileDetectingAsync(ReplApp app, CancellationTokenSource caller)
	{
		var sizes = new WaitingWindowSizeProvider();
		var host = new StreamedReplHost(new StringWriter(), sizes);
		await using (host.ConfigureAwait(false))
		{
			var run = host.RunSessionAsync(app, cancellationToken: caller.Token).AsTask();
			var first = await Task.WhenAny(sizes.Waiting, run).WaitAsync(Patience).ConfigureAwait(false);
			if (first == run)
			{
				Assert.Fail($"The session ended ({run.Status}) before it detected the terminal: {run.Exception?.GetBaseException()}");
			}

			await caller.CancelAsync().ConfigureAwait(false);

			return await run.WaitAsync(Patience).ConfigureAwait(false);
		}
	}

	// A terminal that never reports its size, signalling once it is asked, until the request is cancelled.
	private sealed class WaitingWindowSizeProvider : IWindowSizeProvider
	{
		private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task Waiting => _waiting.Task;

		public event EventHandler<WindowSizeEventArgs>? SizeChanged
		{
			add { }
			remove { }
		}

		public async ValueTask<(int Width, int Height)?> GetSizeAsync(CancellationToken ct)
		{
			_waiting.TrySetResult();
			await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
			return null;
		}
	}
}
