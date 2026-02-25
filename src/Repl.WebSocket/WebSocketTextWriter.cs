using System.Net.WebSockets;
using System.Text;

namespace Repl.WebSocket;

/// <summary>
/// A <see cref="TextWriter"/> that sends UTF-8 text frames over a <see cref="System.Net.WebSockets.WebSocket"/>.
/// </summary>
public sealed class WebSocketTextWriter(System.Net.WebSockets.WebSocket socket, CancellationToken cancellationToken) : TextWriter
{
	/// <inheritdoc />
	public override Encoding Encoding => Encoding.UTF8;

	/// <inheritdoc />
	public override Task WriteAsync(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return Task.CompletedTask;
		}

		if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
		{
			return Task.CompletedTask;
		}

		var payload = Encoding.UTF8.GetBytes(value);
		return socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
	}

	/// <inheritdoc />
	public override Task WriteLineAsync(string? value) =>
		WriteAsync((value ?? string.Empty) + Environment.NewLine);
}
