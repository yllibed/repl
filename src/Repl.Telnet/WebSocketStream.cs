using System.Net.WebSockets;

namespace Repl.Telnet;

/// <summary>
/// Adapts a <see cref="WebSocket"/> to a bidirectional <see cref="Stream"/>,
/// allowing <see cref="TelnetFraming"/> to operate over WebSocket connections.
/// Binary messages are read/written as a flat byte stream.
/// </summary>
public sealed class WebSocketStream : Stream
{
	private readonly WebSocket _socket;
	private readonly CancellationToken _ct;

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
	public override async ValueTask<int> ReadAsync(
		Memory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken);
		var result = await _socket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
		if (result.MessageType == WebSocketMessageType.Close)
		{
			return 0;
		}

		return result.Count;
	}

	/// <inheritdoc />
	public override async ValueTask WriteAsync(
		ReadOnlyMemory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken);
		if (_socket.State is WebSocketState.Open)
		{
			await _socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, cts.Token)
				.ConfigureAwait(false);
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
}
