using System.IO.Pipelines;
using System.Net.WebSockets;

namespace Repl.IntegrationTests;

using WebSocket = System.Net.WebSockets.WebSocket;

// Two WebSockets connected to each other in memory: a session's end and its peer's.
internal static class InMemoryWebSocketPair
{
	public static (WebSocket Server, WebSocket Client) Create()
	{
		var toServer = new Pipe();
		var toClient = new Pipe();
		var server = WebSocket.CreateFromStream(
			new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream()),
			new WebSocketCreationOptions { IsServer = true });
		var client = WebSocket.CreateFromStream(
			new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream()),
			new WebSocketCreationOptions { IsServer = false });
		return (server, client);
	}

	// Reads until the other end closes the connection, answers its close, and reports the status it closed with.
	public static async Task<WebSocketCloseStatus?> AnswerTheCloseAsync(WebSocket socket)
	{
		var buffer = new byte[4096];
		while (true)
		{
			var result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
			if (result.MessageType == WebSocketMessageType.Close)
			{
				await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None)
					.ConfigureAwait(false);
				return result.CloseStatus;
			}
		}
	}

	// Reads from one stream and writes to another; disposing it ends both, as closing a connection does.
	private sealed class DuplexStream(Stream input, Stream output) : Stream
	{
		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
			input.ReadAsync(buffer, cancellationToken);

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
			output.WriteAsync(buffer, cancellationToken);

		public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

		public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

		public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);

		public override void Flush() => output.Flush();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				input.Dispose();
				output.Dispose();
			}

			base.Dispose(disposing);
		}
	}
}
