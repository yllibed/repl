using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using Repl.Telnet;

namespace Repl.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class Given_TelnetSession
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	[TestMethod]
	[Description("Verifies a Telnet session ends successfully when the peer closes its input, instead of waiting for input that can no longer arrive.")]
	public async Task When_ThePeerClosesItsInput_Then_TheSessionEndsSuccessfully()
	{
		var peer = new TelnetPeer();
		await peer.CloseInputAsync().ConfigureAwait(false);

		var exitCode = await peer.RunAsync(CreateApp(), CancellationToken.None).WaitAsync(Patience).ConfigureAwait(false);

		exitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("Verifies a Telnet session no longer registers its metadata once it has ended, so peers that connect and leave do not accumulate sessions.")]
	public async Task When_TheSessionEnds_Then_ItIsNoLongerRegistered()
	{
		string? sessionId = null;
		var app = CreateApp();
		app.Map("whoami", () => sessionId = ReplSessionIO.CurrentSessionId);
		var peer = new TelnetPeer();
		await peer.SendAsync("whoami\r").ConfigureAwait(false);
		await peer.CloseInputAsync().ConfigureAwait(false);

		var exitCode = await peer.RunAsync(app, CancellationToken.None).WaitAsync(Patience).ConfigureAwait(false);

		exitCode.Should().Be(0);
		sessionId.Should().NotBeNullOrEmpty();
		ReplSessionIO.TryGetSession(sessionId ?? string.Empty, out _).Should().BeFalse();
	}

	[TestMethod]
	[Description("Verifies the caller cancelling a Telnet session at its prompt, with the peer still connected, propagates the cancellation rather than ending as if the peer had left.")]
	public async Task When_TheCallerCancelsAtThePrompt_Then_TheCancellationPropagates()
	{
		using var caller = new CancellationTokenSource();

		var act = () => CancelOnceThePromptShowsAsync(CreateApp(), caller);

		await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Verifies the caller cancelling a Telnet session at its prompt returns the configured cancellation exit code.")]
	public async Task When_TheCallerCancelsAtThePrompt_Then_TheConfiguredCancellationCodeIsReturned()
	{
		var app = CreateApp();
		app.Options(options => options.ExitCodes.Cancelled = 73);
		using var caller = new CancellationTokenSource();

		var exitCode = await CancelOnceThePromptShowsAsync(app, caller).ConfigureAwait(false);

		exitCode.Should().Be(73);
	}

	[TestMethod]
	[Description("Verifies a Telnet session ends once the user exits, even while the peer keeps its side of the connection open.")]
	public async Task When_TheUserExitsWhileThePeerStaysConnected_Then_TheSessionEnds()
	{
		var peer = new TelnetPeer();
		await peer.SendAsync("exit\r").ConfigureAwait(false);

		var exitCode = await peer.RunAsync(CreateApp(), CancellationToken.None).WaitAsync(Patience).ConfigureAwait(false);

		exitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("Verifies a Telnet session whose peer is gone before the opening negotiation can be written ends, instead of waiting for input that can never arrive.")]
	public async Task When_TheOpeningNegotiationCannotBeWritten_Then_TheSessionEnds()
	{
		await using var connection = new ResetConnectionStream();

		var exitCode = await ReplTelnetSession.RunAsync(CreateApp(), connection)
			.AsTask()
			.WaitAsync(Patience)
			.ConfigureAwait(false);

		exitCode.Should().Be(0);
	}

	[TestMethod]
	[Description("Verifies a Telnet session over a WebSocket closes the connection normally once the user exits, rather than aborting it, so the peer sees a normal closure.")]
	public async Task When_TheUserExitsOverAWebSocket_Then_TheConnectionIsClosedNormally()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			var peer = InMemoryWebSocketPair.AnswerTheCloseAsync(client);
			await client.SendAsync(Encoding.ASCII.GetBytes("exit\r"), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None)
				.ConfigureAwait(false);

			var exitCode = await ReplTelnetSession.RunAsync(CreateApp(), server, TelnetPeer.Options)
				.AsTask()
				.WaitAsync(Patience)
				.ConfigureAwait(false);

			exitCode.Should().Be(0);
			(await peer.WaitAsync(Patience).ConfigureAwait(false)).Should().Be(WebSocketCloseStatus.NormalClosure);
			server.State.Should().Be(WebSocketState.Closed);
		}
	}

	[TestMethod]
	[Description("Verifies a Telnet session over a WebSocket still ends once the user exits when the peer never answers the close.")]
	public async Task When_ThePeerNeverAnswersTheClose_Then_TheSessionStillEnds()
	{
		var (server, client) = InMemoryWebSocketPair.Create();
		using (server)
		using (client)
		{
			await client.SendAsync(Encoding.ASCII.GetBytes("exit\r"), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None)
				.ConfigureAwait(false);

			var exitCode = await ReplTelnetSession.RunAsync(CreateApp(), server, TelnetPeer.Options)
				.AsTask()
				.WaitAsync(Patience)
				.ConfigureAwait(false);

			exitCode.Should().Be(0);
		}
	}

	private static ReplApp CreateApp()
	{
		var app = ReplApp.Create();
		app.Map("ping", () => "pong");
		return app;
	}

	// Runs a session whose peer stays connected, then cancels the caller once the prompt shows.
	private static async Task<int> CancelOnceThePromptShowsAsync(ReplApp app, CancellationTokenSource caller)
	{
		var peer = new TelnetPeer();
		var run = peer.RunAsync(app, caller.Token);
		await peer.WaitForOutputAsync(">").WaitAsync(Patience).ConfigureAwait(false);

		await caller.CancelAsync().ConfigureAwait(false);

		return await run.WaitAsync(Patience).ConfigureAwait(false);
	}

	// The peer end of a Telnet connection. The window size is given up front, so the session does not wait for NAWS.
	private sealed class TelnetPeer
	{
		public static ReplRunOptions Options { get; } = new()
		{
			TerminalOverrides = new TerminalSessionOverrides { WindowSize = (80, 24) },
		};

		private readonly Pipe _toSession = new();
		private readonly Pipe _fromSession = new();

		public Task<int> RunAsync(ReplApp app, CancellationToken ct) =>
			ReplTelnetSession.RunAsync(app, new DuplexPipe(_toSession.Reader, _fromSession.Writer), Options, ct).AsTask();

		public async Task SendAsync(string text) =>
			await _toSession.Writer.WriteAsync(Encoding.ASCII.GetBytes(text)).ConfigureAwait(false);

		public ValueTask CloseInputAsync() => _toSession.Writer.CompleteAsync();

		public async Task WaitForOutputAsync(string text)
		{
			var seen = new StringBuilder();
			while (true)
			{
				var result = await _fromSession.Reader.ReadAsync().ConfigureAwait(false);
				foreach (var segment in result.Buffer)
				{
					seen.Append(Encoding.Latin1.GetString(segment.Span));
				}

				_fromSession.Reader.AdvanceTo(result.Buffer.End);
				if (seen.ToString().Contains(text, StringComparison.Ordinal))
				{
					return;
				}

				if (result.IsCompleted)
				{
					Assert.Fail($"The session ended its output before writing '{text}': {seen}");
				}
			}
		}
	}

	private sealed record DuplexPipe(PipeReader Input, PipeWriter Output) : IDuplexPipe;

	// A connection the peer has reset: nothing can be written to it, and nothing arrives from it until it is closed.
	private sealed class ResetConnectionStream : Stream
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

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
			return 0;
		}

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
			ValueTask.FromException(new IOException("The connection was reset by the peer."));

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new IOException("The connection was reset by the peer.");

		public override void Flush()
		{
		}

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();
	}
}
