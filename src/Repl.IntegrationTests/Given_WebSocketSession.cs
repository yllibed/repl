using System.Net.WebSockets;
using System.Text;
using Repl.WebSocket;

namespace Repl.IntegrationTests;

using WebSocket = System.Net.WebSockets.WebSocket;

[TestClass]
[DoNotParallelize]
public sealed class Given_WebSocketSession
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	private static readonly ReplRunOptions s_options = new()
	{
		TerminalOverrides = new TerminalSessionOverrides { WindowSize = (80, 24) },
	};

	[TestMethod]
	[Description("Verifies a WebSocket session closes the connection normally once the user exits, rather than aborting it, so the peer sees a normal closure.")]
	public async Task When_TheUserExits_Then_TheConnectionIsClosedNormally()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			var peer = InMemoryWebSocketPair.AnswerTheCloseAsync(client);
			await client.SendAsync(Encoding.UTF8.GetBytes("exit\r"), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
				.ConfigureAwait(false);

			var exitCode = await ReplWebSocketSession.RunAsync(CreateApp(), server, s_options)
				.AsTask()
				.WaitAsync(Patience)
				.ConfigureAwait(false);

			exitCode.Should().Be(0);
			(await peer.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(WebSocketCloseStatus.NormalClosure);
			server.State.Should().Be(WebSocketState.Closed);
		}
	}

	[TestMethod]
	[Description("Verifies a WebSocket session whose peer never answers the close still ends soon after the user exits, aborting the connection.")]
	public async Task When_ThePeerNeverAnswersTheClose_Then_TheConnectionIsAbortedSoon()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			await client.SendAsync(Encoding.UTF8.GetBytes("exit\r"), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
				.ConfigureAwait(false);
			var elapsed = System.Diagnostics.Stopwatch.StartNew();

			var exitCode = await ReplWebSocketSession.RunAsync(CreateApp(), server, s_options)
				.AsTask()
				.WaitAsync(Patience)
				.ConfigureAwait(false);

			exitCode.Should().Be(0);
			elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
			server.State.Should().Be(WebSocketState.Aborted);
		}
	}

	[TestMethod]
	[Description("Verifies a WebSocket session whose peer closes the connection first ends normally and answers that close.")]
	public async Task When_ThePeerClosesFirst_Then_TheSessionEndsAndAnswersTheClose()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			var run = ReplWebSocketSession.RunAsync(CreateApp(), server, s_options).AsTask();
			await WaitForOutputAsync(client, ">").WaitAsync(Patience).ConfigureAwait(false);

			await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, CancellationToken.None)
				.ConfigureAwait(false);
			var exitCode = await run.WaitAsync(Patience).ConfigureAwait(false);
			var answer = await InMemoryWebSocketPair.ReceiveTheCloseAsync(client).WaitAsync(Patience).ConfigureAwait(false);

			exitCode.Should().Be(0);
			answer.Should().Be(WebSocketCloseStatus.NormalClosure);
			server.State.Should().Be(WebSocketState.Closed);
		}
	}

	[TestMethod]
	[Description("Verifies the caller cancelling a WebSocket session at its prompt still propagates the cancellation, rather than ending as if the user had exited.")]
	public async Task When_TheCallerCancelsAtThePrompt_Then_TheCancellationPropagates()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		using (var caller = new CancellationTokenSource())
		{
			var run = ReplWebSocketSession.RunAsync(CreateApp(), server, s_options, caller.Token).AsTask();
			await WaitForOutputAsync(client, ">").WaitAsync(Patience).ConfigureAwait(false);

			await caller.CancelAsync().ConfigureAwait(false);

			var act = () => run.WaitAsync(Patience);
			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
		}
	}

	[TestMethod]
	[Description("Verifies a WebSocket session still completes the close handshake when the peer sends something before answering the close.")]
	public async Task When_ThePeerSendsDataBeforeAnsweringTheClose_Then_TheConnectionIsStillClosedNormally()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			var peer = InMemoryWebSocketPair.AnswerTheCloseAsync(client, sendFirst: "x");
			await client.SendAsync(Encoding.UTF8.GetBytes("exit\r"), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
				.ConfigureAwait(false);

			var exitCode = await ReplWebSocketSession.RunAsync(CreateApp(), server, s_options)
				.AsTask()
				.WaitAsync(Patience)
				.ConfigureAwait(false);

			exitCode.Should().Be(0);
			(await peer.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(WebSocketCloseStatus.NormalClosure);
			server.State.Should().Be(WebSocketState.Closed);
		}
	}

	[TestMethod]
	[Description("Verifies a WebSocket session whose receive failed still closes the connection normally before the failure surfaces.")]
	public async Task When_TheReceiveFails_Then_TheConnectionIsStillClosedNormally()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			var peer = InMemoryWebSocketPair.AnswerTheCloseAsync(client);
			await client.SendAsync(Encoding.UTF8.GetBytes("@@repl:resize {\"cols\":100,\"rows\":30}"), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
				.ConfigureAwait(false);

			var act = () => ReplWebSocketSession.RunAsync(
					CreateApp(), server, s_options, onControlMessage: _ => throw new InvalidOperationException("The observer failed."), CancellationToken.None)
				.AsTask()
				.WaitAsync(Patience);

			await act.Should().ThrowAsync<InvalidOperationException>().ConfigureAwait(false);
			var stateWhenTheFailureSurfaced = server.State;

			stateWhenTheFailureSurfaced.Should().Be(WebSocketState.Closed);
			(await peer.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(WebSocketCloseStatus.NormalClosure);
		}
	}

	[TestMethod]
	[Description("Verifies a WebSocket session whose token is cancelled before it starts propagates the cancellation and aborts the connection rather than leaving it open.")]
	public async Task When_TheSessionIsCancelledBeforeItStarts_Then_TheConnectionIsAborted()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		using (var caller = new CancellationTokenSource())
		{
			await caller.CancelAsync().ConfigureAwait(false);

			var act = () => ReplWebSocketSession.RunAsync(CreateApp(), server, s_options, caller.Token).AsTask().WaitAsync(Patience);

			await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
			server.State.Should().Be(WebSocketState.Aborted);
		}
	}

	private static ReplApp CreateApp()
	{
		var app = ReplApp.Create();
		app.Map("ping", () => "pong");
		return app;
	}

	// Receives until the given text has been sent.
	private static async Task WaitForOutputAsync(WebSocket socket, string text)
	{
		var buffer = new byte[4096];
		var seen = new StringBuilder();
		while (!seen.ToString().Contains(text, StringComparison.Ordinal))
		{
			var result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
			if (result.MessageType == WebSocketMessageType.Close)
			{
				Assert.Fail($"The session closed before sending '{text}': {seen}");
			}

			seen.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
		}
	}
}
