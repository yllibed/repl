using AwesomeAssertions;
using System.IO.Pipes;
using Microsoft.Extensions.DependencyInjection;
using Repl.Parameters;
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
		var name = TestPipeName.Create();
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

	[TestMethod]
	[Description("A blocked application cancellation callback cannot delay listener shutdown or pipe retirement; active backend ownership remains until its method finishes.")]
	public async Task When_ACancellationCallbackBlocks_Then_ListenerShutdownStillTimesOut()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		using var releaseCallback = new ManualResetEventSlim();
		var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var backend = new BlockedBackend();
		CancellationTokenRegistration registration = default;
		var name = TestPipeName.Create();
		await using var server = new NamedPipeReplServer(name, token =>
		{
			registration = token.Register(() => { callbackStarted.TrySetResult(); releaseCallback.Wait(); });
			return ValueTask.FromResult<IRemoteReplBackend>(backend);
		});
		await using var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);
		var call = client.DescribeAsync(deadline.Token);
		await backend.Started.Task.WaitAsync(deadline.Token);
		var shutdown = server.DisposeAsync().AsTask();
		try
		{
			await callbackStarted.Task.WaitAsync(deadline.Token);
			var first = await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(8), TimeProvider.System, deadline.Token));

			first.Should().BeSameAs(shutdown, "the listener deadline must include application cancellation callbacks");
#pragma warning disable VSTHRD003 // The assertion observes shutdown started by this test.
			var observe = () => shutdown;
#pragma warning restore VSTHRD003
			await observe.Should().ThrowAsync<TimeoutException>();
			server.Completion.IsCompleted.Should().BeTrue("acceptance stops without waiting for the application callback");
			backend.Disposed.Task.IsCompleted.Should().BeFalse();
			var invocation = () => call.WaitAsync(deadline.Token);
			await invocation.Should().ThrowAsync<ConnectionLostException>();
			using var connectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
			var reconnect = () => NamedPipeReplServer.ConnectAsync(name, connectionDeadline.Token);
			await reconnect.Should().ThrowAsync<OperationCanceledException>();
		}
		finally
		{
			releaseCallback.Set();
			backend.Release.TrySetResult();
			try { await shutdown.WaitAsync(deadline.Token); } catch (TimeoutException) { }
			try { await call.WaitAsync(deadline.Token); } catch (ConnectionLostException) { }
			await registration.DisposeAsync();
		}

		await backend.Disposed.Task.WaitAsync(deadline.Token);
		backend.DisposedWhileRunning.Should().BeFalse();
	}

	[TestMethod]
	[DataRow(false, DisplayName = "Owned pipe disconnects")]
	[DataRow(true, DisplayName = "Borrowed pipe stays open")]
	[Description("A terminal close has a local deadline even when the backend delays completion; the failed RPC connection is retired without disposing borrowed streams.")]
	public async Task When_ATerminalDelaysClosing_Then_TheClientTimesOutLocally(bool borrowStream)
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
		var backend = new DelayedTerminal();
		var name = TestPipeName.Create();
		await using var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(backend));
		await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		if (borrowStream) { await pipe.ConnectAsync(deadline.Token); }
		await using var client = borrowStream ? new RemoteReplClient(pipe) : await NamedPipeReplServer.ConnectAsync(name, deadline.Token);
		await using var terminal = await client.OpenTerminalAsync(cancellationToken: deadline.Token);
		try
		{
			var close = terminal.DisposeAsync().AsTask();
			await backend.Cancelled.Task.WaitAsync(deadline.Token);
			var first = await Task.WhenAny(close, Task.Delay(TimeSpan.FromSeconds(8), TimeProvider.System, deadline.Token));

			first.Should().BeSameAs(close, "the client's own close deadline must expire before the outer test deadline");
#pragma warning disable VSTHRD003 // The assertion observes the close started by this test.
			var observe = () => close;
#pragma warning restore VSTHRD003
			await observe.Should().ThrowAsync<TimeoutException>();
#pragma warning disable VSTHRD003 // The remote terminal completes independently of the test thread.
			var completion = () => terminal.Completion;
#pragma warning restore VSTHRD003
			await completion.Should().ThrowAsync<TimeoutException>();
			if (borrowStream) { pipe.CanRead.Should().BeTrue(); }
			backend.Disposed.Task.IsCompleted.Should().BeFalse();
		}
		finally { backend.Release.TrySetResult(130); }

		await backend.Disposed.Task.WaitAsync(deadline.Token);
	}

	[TestMethod]
	[Description("A slow client's full output buffer closes its factory-owned pipe, cancelling the active handler and releasing its DI scope before the client is explicitly disposed.")]
	public async Task When_TheClientOutputOverflows_Then_TheServerReleasesItsSession()
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var app = ReplApp.Create(services => services.AddScoped(_ => new TrackedScope(disposed))).UseEmbeddedConsoleProfile();
		app.Map("flood", async (IReplIoContext io, [FromServices] TrackedScope scope, CancellationToken ct) =>
		{
			started.TrySetResult();
			try
			{
				while (true) { await io.Output.WriteAsync("chunk".AsMemory(), ct).ConfigureAwait(false); }
			}
			finally { stopped.TrySetResult(); }
		});
		var name = TestPipeName.Create();
		await using var server = new NamedPipeReplServer(name, _ => ValueTask.FromResult<IRemoteReplBackend>(new ReplAppRemoteBackend(app)));
		await using var client = await NamedPipeReplServer.ConnectAsync(name, deadline.Token);
		await using var terminal = await client.OpenTerminalAsync(cancellationToken: deadline.Token);

		try { await terminal.InputAsync("flood\r", deadline.Token); }
		catch (ConnectionLostException) { }
		await started.Task.WaitAsync(deadline.Token);
		var completion = () => terminal.Completion.WaitAsync(deadline.Token);
		await completion.Should().ThrowAsync<IOException>().WithMessage("*buffer*");

		await stopped.Task.WaitAsync(deadline.Token);
		await disposed.Task.WaitAsync(deadline.Token);
	}

	private sealed class TrackedScope(TaskCompletionSource disposed) : IAsyncDisposable
	{
		public ValueTask DisposeAsync() { disposed.TrySetResult(); return ValueTask.CompletedTask; }
	}

	private sealed class DelayedTerminal : IRemoteReplBackend, IRemoteTerminalSession
	{
		internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource<int> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private CancellationTokenRegistration _registration;
		public Task<int> Completion => Release.Task;
		public ValueTask<RemoteCommandGraph> DescribeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new RemoteCommandGraph("delayed", [], []));
		public ValueTask<RemoteExecutionResult> ExecuteAsync(RemoteInvocationRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new RemoteExecutionResult(0, "success"));
		public ValueTask<IRemoteTerminalSession> OpenTerminalAsync(RemoteTerminalOptions options, Func<string, CancellationToken, ValueTask> writeOutput, CancellationToken cancellationToken)
		{
			_registration = cancellationToken.Register(() => Cancelled.TrySetResult());
			return ValueTask.FromResult<IRemoteTerminalSession>(this);
		}
		public ValueTask InputAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask InterruptAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask DisposeAsync() { _registration.Dispose(); Disposed.TrySetResult(); return ValueTask.CompletedTask; }
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
