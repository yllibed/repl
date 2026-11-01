using AwesomeAssertions;
using HostingRemoteSample;
using Microsoft.Extensions.Hosting;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_HostingSessions
{
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
