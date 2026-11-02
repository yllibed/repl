using AwesomeAssertions;
using HostingRemoteSample;
using Repl.Samples;
using Microsoft.Extensions.Hosting;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_HostingSessions
{
	[TestMethod]
	[DataRow(true, DisplayName = "Host stopping with callback failure")]
	[DataRow(false, DisplayName = "Disconnect with callback failure")]
	[Description("Cancellation callback errors remain observable without retaining a closed session in the registry or operator session list.")]
	public async Task When_ACancellationCallbackThrows_Then_TheSessionIsStillRemoved(bool hostStopping)
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		using var lifetime = new HostLifetime();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var app = ReplApp.Create().UseEmbeddedConsoleProfile();
		app.Map("wait", async (CancellationToken ct) =>
		{
			var wait = Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, ct);
			using var callback = ct.Register(() => throw new InvalidOperationException("callback failure"));
			entered.SetResult();
			await wait.ConfigureAwait(false);
		});
		var tracker = new SessionTracker();
		tracker.Add("one", "signalr", remotePeer: null);
		var sessions = new SignalRSessions(app, app.Services, lifetime, tracker);
		await using var host = new StreamedReplHost(TextWriter.Null);
		sessions.Start("one", host, "one", new ReplRunOptions
		{
			TerminalOverrides = new TerminalSessionOverrides { AnsiSupported = false, WindowSize = (80, 24) },
		}, CancellationToken.None);
		host.EnqueueInput("wait\n");
		await entered.Task.WaitAsync(deadline.Token);

		if (hostStopping) { lifetime.StopApplication(); }
		Func<Task> close = () => (hostStopping ? sessions.DrainAsync() : sessions.DisconnectAsync("one")).WaitAsync(deadline.Token);
		await close.Should().ThrowAsync<AggregateException>();

		sessions.TryGet("one", out _, out _).Should().BeFalse();
		tracker.GetAllNames().Should().NotContain("one");
	}

	[TestMethod]
	[DataRow(true, DisplayName = "Host stopping")]
	[DataRow(false, DisplayName = "Transport disconnected")]
	[Description("A blocking command callback cannot hold a host or transport notification, while session disposal still waits for that callback to finish.")]
	public async Task When_ACancellationCallbackBlocks_Then_TheNotificationReturnsAndDisposalWaits(bool hostStopping)
	{
		using var notification = new CancellationTokenSource();
		var session = new SessionLifetime(
			hostStopping ? notification.Token : CancellationToken.None,
			hostStopping ? CancellationToken.None : notification.Token);
		using var release = new ManualResetEventSlim();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var callback = session.Token.Register(() =>
		{
			entered.SetResult();
#pragma warning disable VSTHRD002 // The test releases this deliberately blocking command callback in finally.
			release.Wait();
#pragma warning restore VSTHRD002
		});
		Task? disposal = null;
		try
		{
			await Task.Run(() => notification.Cancel(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System);
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System);
			disposal = session.DisposeAsync().AsTask();

			disposal.IsCompleted.Should().BeFalse();
			release.Set();
			await disposal.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System);
		}
		finally
		{
			release.Set();
			await (disposal ?? session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System);
		}
	}

	[TestMethod]
	[Description("Disconnect and host shutdown both wait for active command cleanup before releasing shared services, and shutdown refuses later sessions.")]
	public async Task When_DisconnectRacesShutdown_Then_BothWaitForCommandCleanup()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		using var lifetime = new HostLifetime();

		var app = ReplApp.Create().UseEmbeddedConsoleProfile();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		app.Map("wait", async (CancellationToken ct) =>
		{
			entered.SetResult();
			try { await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, ct).ConfigureAwait(false); }
			finally
			{
				cleaning.SetResult();
#pragma warning disable VSTHRD003 // The test controls completion to verify that cleanup remains awaited.
				await release.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
		});
		var sessions = new SignalRSessions(app, app.Services, lifetime, new SessionTracker());
		await using var host = new StreamedReplHost(TextWriter.Null);
		sessions.Start("one", host, "one", new ReplRunOptions
		{
			TerminalOverrides = new TerminalSessionOverrides { AnsiSupported = false, WindowSize = (80, 24) },
		}, CancellationToken.None);
		host.EnqueueInput("wait\n");
		try
		{
			await entered.Task.WaitAsync(deadline.Token);
			var disconnect = sessions.DisconnectAsync("one");
			await cleaning.Task.WaitAsync(deadline.Token);
			var drain = sessions.DrainAsync();

			disconnect.IsCompleted.Should().BeFalse();
			drain.IsCompleted.Should().BeFalse();
			sessions.TryGet("one", out _, out _).Should().BeTrue();
			Action start = () => sessions.Start("two", host, "two", new ReplRunOptions(), CancellationToken.None);
			start.Should().Throw<OperationCanceledException>();

			release.SetResult();
			await Task.WhenAll(disconnect, drain).WaitAsync(deadline.Token);
			sessions.TryGet("one", out _, out _).Should().BeFalse();
		}
		finally
		{
			release.TrySetResult();
			await sessions.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System);
		}
	}

	private sealed class HostLifetime : IHostApplicationLifetime, IDisposable
	{
		private readonly CancellationTokenSource _stopping = new();
		public CancellationToken ApplicationStarted => CancellationToken.None;
		public CancellationToken ApplicationStopping => _stopping.Token;
		public CancellationToken ApplicationStopped => CancellationToken.None;
		public void StopApplication() => _stopping.Cancel();
		public void Dispose() => _stopping.Dispose();
	}
}
