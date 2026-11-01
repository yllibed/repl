namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_HostAbstraction
{
	[TestMethod]
	[Description("Regression guard: verifies custom host I/O is honored so that apps can run without using the process console streams directly.")]
	public void When_RunningWithCustomHost_Then_InputAndOutputAreRoutedThroughHost()
	{
		var input = new StringReader("ping" + Environment.NewLine);
		var output = new StringWriter();
		var host = new InMemoryHost(input, output);

		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("ping", () => "pong");

		var exitCode = sut.Run(Array.Empty<string>(), host, new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.None });

		exitCode.Should().Be(0);
		output.ToString().Should().Contain("pong");
	}

	[TestMethod]
	[Description("Regression guard: verifies async custom host run so that host-based executions can be awaited.")]
	public async Task When_RunningAsyncWithCustomHost_Then_InputAndOutputAreRoutedThroughHost()
	{
		var input = new StringReader("ping" + Environment.NewLine);
		var output = new StringWriter();
		var host = new InMemoryHost(input, output);

		var sut = ReplApp.Create().UseDefaultInteractive();
		sut.Map("ping", () => "pong");

		var exitCode = await sut.RunAsync(
			Array.Empty<string>(),
			host,
			new ReplRunOptions { HostedServiceLifecycle = HostedServiceLifecycleMode.None });

		exitCode.Should().Be(0);
		output.ToString().Should().Contain("pong");
	}

	[TestMethod]
	[Description("A transport can interrupt one hosted command without signalling the process or cancelling another session using the same app and provider.")]
	public async Task When_AStreamedHostIsInterrupted_Then_OnlyItsCommandIsCancelled()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var app = ReplApp.Create().UseEmbeddedConsoleProfile();
		app.Map("first", (CancellationToken ct) => WaitUntilCancelledAsync(firstStarted, firstStopped, ct));
		app.Map("second", (CancellationToken ct) => WaitUntilCancelledAsync(secondStarted, secondStopped, ct));
		await using var first = new StreamedReplHost(TextWriter.Null, new FixedSize());
		await using var second = new StreamedReplHost(TextWriter.Null, new FixedSize());
		var options = new ReplRunOptions { SessionScope = SessionScopeBehavior.CallerOwned, AnsiSupport = AnsiMode.Never };
		var firstRun = first.RunSessionAsync(app, app.Services, options, lifetime.Token).AsTask();
		var secondRun = second.RunSessionAsync(app, app.Services, options, lifetime.Token).AsTask();
		try
		{
			first.EnqueueInput("first\r");
			second.EnqueueInput("second\r");
			await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(deadline.Token);

			first.Interrupt().Should().BeTrue();
			await firstStopped.Task.WaitAsync(deadline.Token);

			secondStopped.Task.IsCompleted.Should().BeFalse();
			firstRun.IsCompleted.Should().BeFalse();
		}
		finally
		{
			await lifetime.CancelAsync();
			try { await Task.WhenAll(firstRun, secondRun); }
			catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
		}
	}

	private static async Task WaitUntilCancelledAsync(TaskCompletionSource started, TaskCompletionSource stopped, CancellationToken ct)
	{
		started.TrySetResult();
		try { await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, ct).ConfigureAwait(false); }
		finally { stopped.TrySetResult(); }
	}

	private sealed class FixedSize : IWindowSizeProvider
	{
		public ValueTask<(int Width, int Height)?> GetSizeAsync(CancellationToken ct) => ValueTask.FromResult<(int, int)?>((80, 24));
		public event EventHandler<WindowSizeEventArgs>? SizeChanged { add { } remove { } }
	}

	private sealed class InMemoryHost(TextReader input, TextWriter output) : IReplHost
	{
		public TextReader Input { get; } = input;

		public TextWriter Output { get; } = output;
	}
}
