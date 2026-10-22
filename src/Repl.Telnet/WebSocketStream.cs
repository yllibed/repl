using System.Net.WebSockets;

namespace Repl.Telnet;

/// <summary>
/// Adapts a <see cref="WebSocket"/> to a bidirectional <see cref="Stream"/>,
/// allowing <see cref="TelnetFraming"/> to operate over WebSocket connections.
/// Binary messages are read/written as a flat byte stream.
/// </summary>
public sealed class WebSocketStream : Stream
{
	// How long a peer has to answer the stream's close before its connection is aborted.
	private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);

	private readonly WebSocket _socket;
	private readonly CancellationToken _ct;
	// The stream's token, which a close the peer does not answer in time cancels too, aborting the connection.
	private readonly CancellationTokenSource _lifetime;
	private int _closingStarted;
	private Task _closing = Task.CompletedTask;

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
	/// second. The stream's own token still aborts the connection at once. A failed connection is reported as an
	/// <see cref="IOException"/>.
	/// </remarks>
	public override async ValueTask<int> ReadAsync(
		Memory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		using var closing = cancellationToken.Register(
			static state => ((WebSocketStream)state!).BeginClosing(awaitAnswer: false), this);
		try
		{
			var result = await _socket.ReceiveAsync(buffer, _lifetime.Token).ConfigureAwait(false);
			return result.MessageType == WebSocketMessageType.Close ? 0 : result.Count;
		}
		catch (WebSocketException ex)
		{
			throw new IOException(ex.Message, ex);
		}
	}

	/// <inheritdoc />
	/// <remarks>A failed connection is reported as an <see cref="IOException"/>.</remarks>
	public override async ValueTask WriteAsync(
		ReadOnlyMemory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken);
		if (_socket.State is WebSocketState.Open)
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
	/// closes it now, and aborts the connection if the peer does not answer in time, as for a cancelled read.
	/// </remarks>
	public override async ValueTask DisposeAsync()
	{
		// No read is cancelled to receive the peer's answer here, so the close waits for that answer itself.
		BeginClosing(awaitAnswer: true);
		try
		{
#pragma warning disable VSTHRD003 // The close is this stream's own, started here or by a cancelled read.
			await _closing.ConfigureAwait(false);
#pragma warning restore VSTHRD003
		}
		finally
		{
			await base.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <inheritdoc />
	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_lifetime.Dispose();
		}

		base.Dispose(disposing);
	}

	// Closes the WebSocket once, aborting it if the peer does not answer in time. A read in progress receives the
	// peer's answer, so only the close is sent then; otherwise the close waits for the answer itself.
	private void BeginClosing(bool awaitAnswer)
	{
		if (Interlocked.Exchange(ref _closingStarted, 1) != 0)
		{
			return;
		}

		_lifetime.CancelAfter(CloseTimeout);
		_closing = CloseAsync(awaitAnswer, _lifetime.Token);
	}

	private async Task CloseAsync(bool awaitAnswer, CancellationToken timeout)
	{
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
		catch (OperationCanceledException)
		{
			// The peer did not answer in time, or the stream's own token was cancelled before the close began.
			_socket.Abort();
		}
		catch (WebSocketException)
		{
			_socket.Abort();
		}
		catch (InvalidOperationException)
		{
			// A read the owner still had pending: the close cannot wait for the answer, so the connection is aborted.
			_socket.Abort();
		}
	}
}
