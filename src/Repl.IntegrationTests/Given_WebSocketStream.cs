using System.Net.WebSockets;
using TelnetWebSocketStream = Repl.Telnet.WebSocketStream;

namespace Repl.IntegrationTests;

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

}
