using System.Net.WebSockets;
using TelnetWebSocketStream = Repl.Telnet.WebSocketStream;

namespace Repl.IntegrationTests;

using WebSocket = System.Net.WebSockets.WebSocket;

[TestClass]
public sealed class Given_WebSocketStream
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	[TestMethod]
	[Description("Verifies a WebSocket stream whose connection is reset fails its read with an IOException, as a stream does, rather than with the socket's own exception type.")]
	public async Task When_TheConnectionIsReset_Then_ReadingFailsWithAnIOException()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			var stream = new TelnetWebSocketStream(server);
			await using (stream.ConfigureAwait(false))
			{
				client.Abort();

				var act = async () => await stream.ReadAsync(new byte[16]).AsTask().WaitAsync(Patience).ConfigureAwait(false);

				await act.Should().ThrowAsync<IOException>().ConfigureAwait(false);
			}
		}
	}

	[TestMethod]
	[Description("Verifies disposing a WebSocket stream with no read pending closes its connection normally, so the peer sees a normal closure rather than an aborted connection.")]
	public async Task When_TheStreamIsDisposedWithNoReadPending_Then_TheConnectionIsClosedNormally()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			var peer = InMemoryWebSocketPair.AnswerTheCloseAsync(client);
			var stream = new TelnetWebSocketStream(server);

			await stream.DisposeAsync().AsTask().WaitAsync(Patience).ConfigureAwait(false);

			(await peer.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(WebSocketCloseStatus.NormalClosure);
			server.State.Should().Be(WebSocketState.Closed);
		}
	}

	[TestMethod]
	[Description("Verifies cancelling a pending read closes the WebSocket normally rather than aborting it: the read ends with the peer's answering close, and disposing the stream leaves the connection closed.")]
	public async Task When_APendingReadIsCancelled_Then_TheConnectionIsClosedNormally()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		using (var reader = new CancellationTokenSource())
		{
			var peer = InMemoryWebSocketPair.AnswerTheCloseAsync(client);
			var stream = new TelnetWebSocketStream(server);
			await using (stream.ConfigureAwait(false))
			{
				var read = stream.ReadAsync(new byte[16], reader.Token).AsTask();

				await reader.CancelAsync().ConfigureAwait(false);

				(await read.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(0);
			}

			(await peer.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(WebSocketCloseStatus.NormalClosure);
			server.State.Should().Be(WebSocketState.Closed);
		}
	}

	[TestMethod]
	[Description("Verifies a WebSocket stream whose send fails reports it as an IOException, as a stream does, rather than with the socket's own exception type.")]
	public async Task When_ASendFails_Then_WritingFailsWithAnIOException()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (client)
		using (var failing = new FailingSendWebSocket(server))
		{
			var stream = new TelnetWebSocketStream(failing);
			await using (stream.ConfigureAwait(false))
			{
				var act = async () => await stream.WriteAsync(new byte[] { 1, 2, 3 }).AsTask().WaitAsync(Patience).ConfigureAwait(false);

				await act.Should().ThrowAsync<IOException>().ConfigureAwait(false);
			}
		}
	}

	[TestMethod]
	[Description("Verifies a cancelled read whose close the peer never answers still ends once the stream is disposed, aborting the connection after the close's grace period rather than leaving the read pending.")]
	public async Task When_ACancelledReadIsFollowedByDisposal_Then_AnUnansweredCloseStillEndsTheRead()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		using (var reader = new CancellationTokenSource())
		{
			var stream = new TelnetWebSocketStream(server);
			var read = stream.ReadAsync(new byte[16], reader.Token).AsTask();

			await reader.CancelAsync().ConfigureAwait(false);
			await stream.DisposeAsync().AsTask().WaitAsync(Patience).ConfigureAwait(false);

			var act = () => read.WaitAsync(Patience);
			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
			server.State.Should().Be(WebSocketState.Aborted);
		}
	}

	[TestMethod]
	[Description("Verifies disposing a WebSocket stream while a cancelled read is still sending its close waits for that close, rather than returning with the close still under way.")]
	public async Task When_TheStreamIsDisposedWhileACancelledReadSendsItsClose_Then_DisposalWaitsForTheClose()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (client)
		using (var paused = new PausedCloseWebSocket(server))
		using (var reader = new CancellationTokenSource())
		{
			var peer = InMemoryWebSocketPair.AnswerTheCloseAsync(client);
			var stream = new TelnetWebSocketStream(paused);
			var read = stream.ReadAsync(new byte[16], reader.Token).AsTask();
			var cancelling = Task.Run(reader.Cancel);
			await paused.Closing.WaitAsync(Patience).ConfigureAwait(false);

			var disposal = stream.DisposeAsync().AsTask();
			var disposedWhileClosing = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromMilliseconds(200))).ConfigureAwait(false) == disposal;
			paused.Resume();
			await disposal.WaitAsync(Patience).ConfigureAwait(false);
			await cancelling.WaitAsync(Patience).ConfigureAwait(false);

			disposedWhileClosing.Should().BeFalse("disposal must wait for the close the cancelled read began");
			(await read.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(0);
			(await peer.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(WebSocketCloseStatus.NormalClosure);
		}
	}

	[TestMethod]
	[Description("Verifies disposing a WebSocket stream while a read nobody cancelled is pending closes the connection normally, the read ending with the peer's answer.")]
	public async Task When_TheStreamIsDisposedWithAReadPending_Then_TheReadEndsWithTheClose()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			var peer = InMemoryWebSocketPair.AnswerTheCloseAsync(client);
			var stream = new TelnetWebSocketStream(server);
			var read = stream.ReadAsync(new byte[16]).AsTask();

			await stream.DisposeAsync().AsTask().WaitAsync(Patience).ConfigureAwait(false);

			(await read.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(0);
			(await peer.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(WebSocketCloseStatus.NormalClosure);
			server.State.Should().Be(WebSocketState.Closed);
		}
	}

	[TestMethod]
	[Description("Verifies a read cancelled after its stream was disposed without waiting does not fail the caller who cancels it, and that disposing twice does not fail either.")]
	public async Task When_AReadIsCancelledAfterTheStreamWasDisposed_Then_NothingFails()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (client)
		using (var stuck = new StuckReceiveWebSocket(server))
		using (var reader = new CancellationTokenSource())
		{
			var stream = new TelnetWebSocketStream(stuck);
			var read = stream.ReadAsync(new byte[16], reader.Token).AsTask();
#pragma warning disable MA0042, VSTHRD103 // The synchronous disposal is what this test exercises.
			stream.Dispose();
			Action disposeAgain = stream.Dispose;
#pragma warning restore MA0042, VSTHRD103
			Action cancel = reader.Cancel;

			disposeAgain.Should().NotThrow();
			cancel.Should().NotThrow();
			stuck.Release();
			await read.ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(Patience).ConfigureAwait(false);
		}
	}

	[TestMethod]
	[Description("Verifies disposing a WebSocket stream without waiting, while a close the peer never takes is still under way, aborts that close rather than leaving it, and a later asynchronous disposal, pending forever.")]
	public async Task When_TheStreamIsDisposedWhileItsCloseIsStuck_Then_TheCloseIsAborted()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (client)
		using (var stuck = new StuckCloseWebSocket(server))
		using (var reader = new CancellationTokenSource())
		{
			var stream = new TelnetWebSocketStream(stuck);
			var read = stream.ReadAsync(new byte[16], reader.Token).AsTask();
			await reader.CancelAsync().ConfigureAwait(false);
			await stuck.Closing.WaitAsync(Patience).ConfigureAwait(false);
			await client.SendAsync("x"u8.ToArray(), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None)
				.ConfigureAwait(false);
			(await read.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(1);

#pragma warning disable MA0042, VSTHRD103 // The synchronous disposal is what this test exercises.
			stream.Dispose();
#pragma warning restore MA0042, VSTHRD103

			await stream.DisposeAsync().AsTask().WaitAsync(Patience).ConfigureAwait(false);
			stuck.State.Should().Be(WebSocketState.Aborted);
		}
	}

	[TestMethod]
	[DataRow(true, DisplayName = "Peer answers the close")]
	[DataRow(false, DisplayName = "Peer never answers the close")]
	[Description("Verifies disposing a WebSocket stream finishes the close handshake even when its pending read gets data instead of the peer's answer: a peer that answers ends up closed, one that does not is aborted.")]
	public async Task When_APendingReadGetsDataDuringDisposal_Then_TheCloseStillFinishes(bool peerAnswers)
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			var stream = new TelnetWebSocketStream(server);
			var read = stream.ReadAsync(new byte[16]).AsTask();
			var disposal = stream.DisposeAsync().AsTask();
			await InMemoryWebSocketPair.ReceiveTheCloseAsync(client).WaitAsync(Patience).ConfigureAwait(false);
			await client.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None)
				.ConfigureAwait(false);
			if (peerAnswers)
			{
				await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None)
					.ConfigureAwait(false);
			}

			(await read.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(1);
			await disposal.WaitAsync(Patience).ConfigureAwait(false);

			server.State.Should().Be(peerAnswers ? WebSocketState.Closed : WebSocketState.Aborted);
		}
	}

	[TestMethod]
	[Description("Verifies a WebSocket stream refuses a read once it was disposed, rather than starting a receive nothing will end.")]
	public async Task When_AStreamWasDisposed_Then_ReadingFails()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			var stream = new TelnetWebSocketStream(server);
#pragma warning disable MA0042, VSTHRD103 // The synchronous disposal is what this test exercises.
			stream.Dispose();
#pragma warning restore MA0042, VSTHRD103

			var act = async () => await stream.ReadAsync(new byte[16]).AsTask().WaitAsync(Patience).ConfigureAwait(false);

			(await act.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(false))
				.Which.ObjectName.Should().Be(typeof(TelnetWebSocketStream).FullName, "the stream refuses the read, before it reaches its disposed parts");
		}
	}

	[TestMethod]
	[Description("Verifies a WebSocket stream refuses a read that starts while it is being disposed, leaving the close it is finishing to end normally.")]
	public async Task When_AReadStartsDuringDisposal_Then_ItIsRefusedAndTheCloseStillFinishes()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			var stream = new TelnetWebSocketStream(server);
			var disposal = stream.DisposeAsync().AsTask();
			await InMemoryWebSocketPair.ReceiveTheCloseAsync(client).WaitAsync(Patience).ConfigureAwait(false);

			var act = async () => await stream.ReadAsync(new byte[16]).AsTask().WaitAsync(Patience).ConfigureAwait(false);

			await act.Should().ThrowAsync<ObjectDisposedException>().ConfigureAwait(false);
			await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None)
				.ConfigureAwait(false);
			await disposal.WaitAsync(Patience).ConfigureAwait(false);
			server.State.Should().Be(WebSocketState.Closed);
		}
	}

	[TestMethod]
	[Description("Verifies a cancelled read that gets data instead of the peer's answer still ends with the connection closed in time: a peer that never answers is aborted, even with no read pending and the stream not yet disposed.")]
	public async Task When_ACancelledReadGetsDataAndThePeerNeverAnswers_Then_TheConnectionIsAbortedInTime()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		using (var reader = new CancellationTokenSource())
		{
			var stream = new TelnetWebSocketStream(server);
			await using (stream.ConfigureAwait(false))
			{
				var read = stream.ReadAsync(new byte[16], reader.Token).AsTask();
				await reader.CancelAsync().ConfigureAwait(false);
				await InMemoryWebSocketPair.ReceiveTheCloseAsync(client).WaitAsync(Patience).ConfigureAwait(false);
				await client.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None)
					.ConfigureAwait(false);
				(await read.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(1);

				// The close's deadline is a second; three leave room for a slow machine without hiding a lost deadline.
				await WaitForStateAsync(server, WebSocketState.Aborted, TimeSpan.FromSeconds(3)).ConfigureAwait(false);

				server.State.Should().Be(WebSocketState.Aborted);
			}
		}
	}

	[TestMethod]
	[Description("Verifies disposing a WebSocket stream without waiting, after a cancelled read got data instead of the peer's answer, aborts the connection rather than leaving its close unfinished.")]
	public async Task When_AStreamIsDisposedAfterACancelledReadGotData_Then_TheConnectionIsAborted()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		using (var reader = new CancellationTokenSource())
		{
			var stream = new TelnetWebSocketStream(server);
			var read = stream.ReadAsync(new byte[16], reader.Token).AsTask();
			await reader.CancelAsync().ConfigureAwait(false);
			await InMemoryWebSocketPair.ReceiveTheCloseAsync(client).WaitAsync(Patience).ConfigureAwait(false);
			await client.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None)
				.ConfigureAwait(false);
			(await read.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(1);

#pragma warning disable MA0042, VSTHRD103 // The synchronous disposal is what this test exercises.
			stream.Dispose();
#pragma warning restore MA0042, VSTHRD103

			server.State.Should().Be(WebSocketState.Aborted);
		}
	}

	[TestMethod]
	[Description("Verifies two concurrent asynchronous disposals of a WebSocket stream finish its close once, so a peer that answers still sees a normal closure.")]
	public async Task When_AStreamIsDisposedTwiceAtOnce_Then_TheCloseFinishesNormally()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		using (var reader = new CancellationTokenSource())
		{
			// A cancelled read that gets data leaves the close sent but unanswered, for the disposals to finish.
			var stream = new TelnetWebSocketStream(server);
			var read = stream.ReadAsync(new byte[16], reader.Token).AsTask();
			await reader.CancelAsync().ConfigureAwait(false);
			await InMemoryWebSocketPair.ReceiveTheCloseAsync(client).WaitAsync(Patience).ConfigureAwait(false);
			await client.SendAsync(new byte[] { 1 }, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None)
				.ConfigureAwait(false);
			(await read.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(1);

			var first = stream.DisposeAsync().AsTask();
			var second = stream.DisposeAsync().AsTask();
			await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None)
				.ConfigureAwait(false);
			await Task.WhenAll(first, second).WaitAsync(Patience).ConfigureAwait(false);

			server.State.Should().Be(WebSocketState.Closed);
		}
	}

	// Waits until the socket reaches the state, or the time is up.
	private static async Task WaitForStateAsync(WebSocket socket, WebSocketState state, TimeSpan within)
	{
		var elapsed = System.Diagnostics.Stopwatch.StartNew();
		while (socket.State != state && elapsed.Elapsed < within)
		{
			await Task.Delay(50).ConfigureAwait(false);
		}
	}

	// A WebSocket whose receive ignores its cancellation until the test releases it.
	private sealed class StuckReceiveWebSocket(WebSocket inner) : DelegatingWebSocket(inner)
	{
		private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void Release() => _released.TrySetResult();

		public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
		{
#pragma warning disable VSTHRD003 // The release the test signals is what this receive waits for.
			await _released.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
		}
	}

	// A WebSocket whose close is never taken by the peer: it waits until it is cancelled.
	private sealed class StuckCloseWebSocket(WebSocket inner) : DelegatingWebSocket(inner)
	{
		private readonly TaskCompletionSource _closing = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task Closing => _closing.Task;

		public override async Task CloseOutputAsync(
			WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
		{
			_closing.TrySetResult();
			await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
		}
	}

	// A WebSocket whose sends fail as a reset connection's do.
	private sealed class FailingSendWebSocket(WebSocket inner) : DelegatingWebSocket(inner)
	{
		public override WebSocketState State => WebSocketState.Open;

		public override ValueTask SendAsync(
			ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
			ValueTask.FromException(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
	}

	// A WebSocket whose close is held, inside the call that starts it, until the test resumes it.
	private sealed class PausedCloseWebSocket(WebSocket inner) : DelegatingWebSocket(inner)
	{
		private readonly TaskCompletionSource _closing = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly ManualResetEventSlim _resume = new();

		public Task Closing => _closing.Task;

		public void Resume() => _resume.Set();

		public override Task CloseOutputAsync(
			WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
		{
			_closing.TrySetResult();
			_resume.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
			return base.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
		}

		public override void Dispose()
		{
			_resume.Dispose();
			base.Dispose();
		}
	}
}
