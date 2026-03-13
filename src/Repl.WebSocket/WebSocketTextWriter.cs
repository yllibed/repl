using System.Net.WebSockets;
using System.Text;

namespace Repl.WebSocket;

/// <summary>
/// A <see cref="TextWriter"/> that sends UTF-8 text frames over a <see cref="System.Net.WebSockets.WebSocket"/>.
/// Synchronous <see cref="Write(string?)"/> calls are buffered and sent on <see cref="FlushAsync()"/>.
/// </summary>
public sealed class WebSocketTextWriter(System.Net.WebSockets.WebSocket socket, CancellationToken cancellationToken) : TextWriter
{
	private readonly Lock _lock = new();
	private readonly StringBuilder _buffer = new();

	/// <inheritdoc />
	public override Encoding Encoding => Encoding.UTF8;

	/// <inheritdoc />
	public override void Write(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return;
		}

		lock (_lock) { _buffer.Append(value); }
	}

	/// <inheritdoc />
	public override void Write(char value)
	{
		lock (_lock) { _buffer.Append(value); }
	}

	/// <inheritdoc />
	public override void WriteLine(string? value)
	{
		lock (_lock)
		{
			_buffer.Append(value ?? string.Empty);
			_buffer.Append(Environment.NewLine);
		}
	}

	/// <inheritdoc />
	public override async Task WriteAsync(string? value)
	{
		await FlushBufferAsync().ConfigureAwait(false);
		if (string.IsNullOrEmpty(value))
		{
			return;
		}

		await SendAsync(value).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public override Task WriteLineAsync(string? value) =>
		WriteAsync((value ?? string.Empty) + Environment.NewLine);

	/// <inheritdoc />
	public override Task FlushAsync() => FlushBufferAsync();

	private Task FlushBufferAsync()
	{
		string? text;
		lock (_lock)
		{
			if (_buffer.Length == 0)
			{
				return Task.CompletedTask;
			}

			text = _buffer.ToString();
			_buffer.Clear();
		}

		return SendAsync(text);
	}

	private Task SendAsync(string text)
	{
		if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
		{
			return Task.CompletedTask;
		}

		var payload = Encoding.UTF8.GetBytes(text);
		return socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
	}
}
