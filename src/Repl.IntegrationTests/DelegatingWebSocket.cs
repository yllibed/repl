using System.Net.WebSockets;

namespace Repl.IntegrationTests;

using WebSocket = System.Net.WebSockets.WebSocket;

// A WebSocket that forwards to another, so a test can change one of its operations.
internal class DelegatingWebSocket(WebSocket inner) : WebSocket
{
	public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;

	public override string? CloseStatusDescription => inner.CloseStatusDescription;

	public override WebSocketState State => inner.State;

	public override string? SubProtocol => inner.SubProtocol;

	public override void Abort() => inner.Abort();

	public override Task CloseAsync(
		WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
		inner.CloseAsync(closeStatus, statusDescription, cancellationToken);

	public override Task CloseOutputAsync(
		WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
		inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

	public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
		inner.ReceiveAsync(buffer, cancellationToken);

	public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
		inner.ReceiveAsync(buffer, cancellationToken);

	public override Task SendAsync(
		ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
		inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

	public override ValueTask SendAsync(
		ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
		inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

	public override void Dispose() => inner.Dispose();
}
