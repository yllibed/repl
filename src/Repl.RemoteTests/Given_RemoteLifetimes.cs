using AwesomeAssertions;
using Repl.Remote;
using StreamJsonRpc;

namespace Repl.RemoteTests;

[TestClass]
public sealed class Given_RemoteLifetimes
{
	[TestMethod]
	[DataRow(true, DisplayName = "Blocked description")]
	[DataRow(false, DisplayName = "Execution delaying cancellation")]
	[Description("Listener shutdown remains bounded while backend ownership lasts until all in-flight methods really finish.")]
	public async Task When_ABackendDelaysCancellation_Then_ItIsNotDisposedUnderItsRunningMethod(bool describe)
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var backend = new BlockedBackend();
		var name = $"repl-lifetime-{Guid.NewGuid():N}";
		await using var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(backend));
		await using var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);
		var call = describe ? (Task)client.DescribeAsync(deadline.Token) : client.ExecuteAsync(new() { Arguments = ["wait"] }, deadline.Token);
		await backend.Started.Task.WaitAsync(deadline.Token);
		try
		{
			await client.DisposeAsync();
			try { await call; } catch (ConnectionLostException) { }
			var shutdown = () => server.DisposeAsync().AsTask();
			await shutdown.Should().ThrowAsync<TimeoutException>();
			backend.Disposed.Task.IsCompleted.Should().BeFalse();
		}
		finally { backend.Release.TrySetResult(); }

		await backend.Disposed.Task.WaitAsync(deadline.Token);
		backend.DisposedWhileRunning.Should().BeFalse();
	}

	private sealed class BlockedBackend : IRemoteReplBackend
	{
		internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal bool DisposedWhileRunning { get; private set; }
		private bool _running;
		private async Task WaitAsync()
		{
			_running = true;
			Started.TrySetResult();
#pragma warning disable VSTHRD003 // The test deliberately holds the backend independently of its connection.
			try { await Release.Task.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
			finally { _running = false; }
		}
		public async ValueTask<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken)
		{
			await WaitAsync().ConfigureAwait(false);
			return new("blocked", [], []);
		}
		public async ValueTask<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken)
		{
			await WaitAsync().ConfigureAwait(false);
			return new(0, "success");
		}
		public ValueTask<IRemoteTerminalSession> OpenTerminalAsync(RemoteTerminalOptions options, Func<string, CancellationToken, ValueTask> writeOutput, CancellationToken cancellationToken) => throw new NotSupportedException();
		public ValueTask DisposeAsync()
		{
			DisposedWhileRunning = _running;
			Disposed.TrySetResult();
			return ValueTask.CompletedTask;
		}
	}
}
