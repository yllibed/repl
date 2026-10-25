using System.Net.WebSockets;

namespace Repl.Telnet;

/// <summary>
/// Adapts a <see cref="WebSocket"/> to a bidirectional <see cref="Stream"/>,
/// allowing <see cref="TelnetFraming"/> to operate over WebSocket connections.
/// Binary messages are read/written as a flat byte stream.
/// </summary>
public sealed class WebSocketStream : Stream
{
	// How long a peer has to answer the stream's close before its connection is aborted; the remarks on ReadAsync
	// state it too.
	private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);

	private readonly WebSocket _socket;
	private readonly CancellationToken _ct;
	// The stream's token, which a close the peer does not answer in time cancels too, aborting the connection.
	private readonly CancellationTokenSource _lifetime;
	// Guards the close, published once, and the count of receives still pending.
	private readonly Lock _gate = new();
	private Task? _closing;
	private int _receives;
	private TaskCompletionSource? _receivesEnded;
	private bool _disposed;
	// Set as asynchronous disposal begins, so no read starts beside the receive that finishes the close.
	private bool _disposing;
	// The receive that finishes the close, started once however many disposals run.
	private Task? _finishing;

	/// <summary>
	/// Wraps the specified WebSocket as a stream.
	/// </summary>
	/// <param name="socket">An open binary WebSocket.</param>
	/// <param name="ct">Cancellation token for the lifetime of the stream.</param>
	public WebSocketStream(WebSocket socket, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(socket);
		_socket = socket;
		_ct = ct;
		_lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
	}

	/// <inheritdoc />
	public override bool CanRead => true;

	/// <inheritdoc />
	public override bool CanWrite => true;

	/// <inheritdoc />
	public override bool CanSeek => false;

	/// <inheritdoc />
	public override long Length => throw new NotSupportedException();

	/// <inheritdoc />
	public override long Position
	{
		get => throw new NotSupportedException();
		set => throw new NotSupportedException();
	}

	/// <inheritdoc />
	/// <remarks>
	/// A WebSocket aborts a receive that is cancelled, dropping what it still had to send. Cancelling a read through
	/// <paramref name="cancellationToken"/> therefore closes the WebSocket instead: the read returns <c>0</c> once the
	/// peer answers the close (or what the peer sent before answering it), and throws an
	/// <see cref="OperationCanceledException"/> once the connection is aborted because the peer did not answer within a
	/// second. The stream's own token still aborts the connection at once. A read still pending when the stream is
	/// disposed asynchronously ends the same way, as that disposal closes the WebSocket; disposing it synchronously
	/// aborts the read instead. A failed connection is reported as an <see cref="IOException"/>, and a read once the
	/// stream's disposal began as an <see cref="ObjectDisposedException"/>. A cancelled read that returns data instead
	/// leaves the close under way: the connection is still aborted once the second has passed if the peer has not
	/// answered by then.
	/// </remarks>
	public override async ValueTask<int> ReadAsync(
		Memory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		// Checked with the count under the gate Dispose takes, so no receive starts that disposal would not end.
		CancellationToken lifetime;
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
			_receives++;
			lifetime = _lifetime.Token;
		}

		try
		{
			using var closing = cancellationToken.Register(static state => _ = ((WebSocketStream)state!).CloseOnceAsync(), this);
			var result = await _socket.ReceiveAsync(buffer, lifetime).ConfigureAwait(false);
			return result.MessageType == WebSocketMessageType.Close ? 0 : result.Count;
		}
		catch (WebSocketException ex)
		{
			throw new IOException(ex.Message, ex);
		}
		finally
		{
			EndReceive();
		}
	}

	/// <inheritdoc />
	/// <remarks>A failed connection is reported as an <see cref="IOException"/>.</remarks>
	public override async ValueTask WriteAsync(
		ReadOnlyMemory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken);
		// A peer that closed its side may still read the reply to what it sent before closing.
		if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
		{
			try
			{
				await _socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cts.Token)
					.ConfigureAwait(false);
			}
			catch (WebSocketException ex)
			{
				throw new IOException(ex.Message, ex);
			}
		}
	}

	/// <inheritdoc />
	public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <inheritdoc />
	public override void Flush() { }

	/// <inheritdoc />
	public override int Read(byte[] buffer, int offset, int count) =>
		throw new NotSupportedException("Use ReadAsync.");

	/// <inheritdoc />
	public override void Write(byte[] buffer, int offset, int count) =>
		throw new NotSupportedException("Use WriteAsync.");

	/// <inheritdoc />
	public override long Seek(long offset, SeekOrigin origin) =>
		throw new NotSupportedException();

	/// <inheritdoc />
	public override void SetLength(long value) =>
		throw new NotSupportedException();

	/// <inheritdoc />
	/// <remarks>
	/// Closes the WebSocket normally, unless it is already closed: it waits for the close a cancelled read began, or
	/// closes it now, and aborts the connection if the peer does not answer in time, as for a cancelled read. It also
	/// waits for a pending read to end; when that read gets data rather than the peer's answer, what the peer still
	/// sends until its answer is read and discarded.
	/// </remarks>
	public override async ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			_disposing = true;
		}

		try
		{
			await CloseOnceAsync().ConfigureAwait(false);
			// The handshake is finished only once every pending receive has ended, so its own receive never runs beside
			// one; the close's deadline ends a receive the peer never answers.
			Task? receivesEnded = null;
			lock (_gate)
			{
				if (_receives > 0)
				{
					_receivesEnded ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
					receivesEnded = _receivesEnded.Task;
				}
			}

			if (receivesEnded is not null)
			{
				await receivesEnded.ConfigureAwait(false);
			}

			// A synchronous disposal already aborted what was under way, and its token source is gone. Otherwise the
			// handshake's receive is counted as one, so a synchronous disposal meanwhile aborts it rather than dropping
			// its deadline, and it is started once, so concurrent disposals never receive side by side.
			Task? finishing = null;
			lock (_gate)
			{
				if (!_disposed)
				{
					if (_finishing is null)
					{
						_receives++;
						_finishing = FinishCloseCountedAsync(_lifetime.Token);
					}

					finishing = _finishing;
				}
			}

			if (finishing is not null)
			{
				await finishing.ConfigureAwait(false);
			}
		}
		finally
		{
			await base.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <inheritdoc />
	/// <remarks>
	/// Disposing synchronously cannot wait for the peer: it aborts the connection if a read or the close is still under
	/// way, rather than leaving it without the deadline that would end it.
	/// </remarks>
	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			bool abort;
			lock (_gate)
			{
				if (_disposed)
				{
					base.Dispose(disposing);
					return;
				}

				// A read cancelled from now on no longer starts a close on a token about to be disposed. A started close
				// left unfinished, even one whose sending is done, is aborted with the deadline it would lose.
				_disposed = true;
				abort = _receives > 0 || _closing is not null;
			}

			if (abort)
			{
				_lifetime.Cancel();
			}

			_lifetime.Dispose();
		}

		base.Dispose(disposing);
	}

	// Closes the WebSocket once, aborting it if the peer does not answer in time, and returns that close. A pending
	// receive gets the peer's answer, so only the close is sent then; otherwise the close waits for the answer itself.
	// The close is started under the gate, so a concurrent disposal always finds it and waits for it.
	private Task CloseOnceAsync()
	{
		lock (_gate)
		{
			if (_closing is null && !_disposed)
			{
				// The deadline aborts a connection still not closed by then, whether or not a read is left to notice.
				_lifetime.Token.Register(static state => ((WebSocketStream)state!).AbortUnlessClosed(), this);
				_lifetime.CancelAfter(CloseTimeout);
				_closing = CloseAsync(awaitAnswer: _receives == 0, _lifetime.Token);
			}

			return _closing ?? Task.CompletedTask;
		}
	}

	// Finishes the close handshake within the close's deadline: a receive that ended with data rather than the peer's
	// answer leaves that answer to read, and a connection that is still not closed once the deadline passes is aborted.
	// ReplWebSocketSession keeps a copy of this in step: the two assemblies share no internal code.
	private static async Task FinishCloseAsync(WebSocket socket, CancellationToken deadline)
	{
		try
		{
			var discarded = new byte[256];
			while (socket.State is WebSocketState.CloseSent)
			{
				var result = await socket.ReceiveAsync(discarded.AsMemory(), deadline).ConfigureAwait(false);
				if (result.MessageType == WebSocketMessageType.Close)
				{
					break;
				}
			}

			if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
			{
				await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, deadline).ConfigureAwait(false);
			}
		}
		catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidOperationException)
		{
			// The deadline passed or the connection failed: it is aborted below.
		}

		if (socket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
		{
			socket.Abort();
		}
	}

	private async Task FinishCloseCountedAsync(CancellationToken deadline)
	{
		// Leaves the gate DisposeAsync started this under before any receive.
		await Task.Yield();
		try
		{
			await FinishCloseAsync(_socket, deadline).ConfigureAwait(false);
		}
		finally
		{
			EndReceive();
		}
	}

	private void AbortUnlessClosed()
	{
		if (_socket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
		{
			_socket.Abort();
		}
	}

	private void EndReceive()
	{
		TaskCompletionSource? ended = null;
		lock (_gate)
		{
			if (--_receives == 0)
			{
				ended = _receivesEnded;
			}
		}

		ended?.TrySetResult();
	}

	private async Task CloseAsync(bool awaitAnswer, CancellationToken timeout)
	{
		// Leaves the gate CloseOnceAsync started this under before the close, which may wait for the peer.
		await Task.Yield();
		try
		{
			if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
			{
				var closing = awaitAnswer
					? _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, timeout)
					: _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, timeout);
				await closing.ConfigureAwait(false);
			}
		}
		catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidOperationException)
		{
			// The peer did not answer in time, the stream's own token was cancelled before the close began, the
			// connection failed, or the close could not wait for the answer beside the owner's pending read.
			_socket.Abort();
		}
	}
}
