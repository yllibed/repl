using System.IO.Pipelines;
using Repl.Telnet;

namespace Repl.IntegrationTests;

[TestClass]
public sealed class Given_TelnetFraming
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

	[TestMethod]
	[Description("Verifies disposing a running framing ends it without waiting for the peer, and leaves the caller's pipe free to read again rather than with a read still pending.")]
	public async Task When_ARunningFramingIsDisposed_Then_ItStopsReadingThePipe()
	{
		var pipe = new DuplexPipe(new Pipe().Reader, new Pipe().Writer);
		var framing = new TelnetFraming(pipe);
		var run = framing.RunAsync(CancellationToken.None);

		await framing.DisposeAsync().ConfigureAwait(false);
		await run.WaitAsync(Patience).ConfigureAwait(false);

		var readAgain = () => pipe.Input.TryRead(out _);
		readAgain.Should().NotThrow("the framing must not leave a read pending on the caller's pipe");
	}

	[TestMethod]
	[Description("Verifies a framing can be disposed more than once, and that running it after it was disposed ends at once, without failing or writing to the caller's pipe.")]
	public async Task When_AFramingIsDisposedTwiceThenRun_Then_ItEndsWithoutWriting()
	{
		var toPeer = new Pipe();
		var framing = new TelnetFraming(new DuplexPipe(new Pipe().Reader, toPeer.Writer));

		await framing.DisposeAsync().ConfigureAwait(false);
		await framing.DisposeAsync().ConfigureAwait(false);
		var act = () => framing.RunAsync(CancellationToken.None).WaitAsync(Patience);

		await act.Should().NotThrowAsync().ConfigureAwait(false);
		toPeer.Reader.TryRead(out _).Should().BeFalse("a disposed framing has no session to negotiate");
	}

	[TestMethod]
	[Description("Verifies disposing a framing sends the output already written before it stops reading the peer, since stopping a read can end the connection on some transports.")]
	public async Task When_AFramingIsDisposedWithOutputPending_Then_TheOutputIsSentBeforeTheReadStops()
	{
		var fromPeer = new Pipe();
		var toPeer = new Pipe(new PipeOptions(pauseWriterThreshold: 64, resumeWriterThreshold: 32));
		var input = new RecordingPipeReader(fromPeer.Reader);
		var framing = new TelnetFraming(new DuplexPipe(input, toPeer.Writer));
		var run = framing.RunAsync(CancellationToken.None);
		await framing.Output.WriteAsync(new string('x', 1024)).ConfigureAwait(false);

		// The peer has not read the output yet, so disposal cannot have stopped reading.
		var disposal = framing.DisposeAsync().AsTask();
		var stoppedBeforeTheOutputWasRead = input.CancelRequested;
		var received = await ReadAtLeastAsync(toPeer.Reader, 1024).WaitAsync(Patience).ConfigureAwait(false);
		await disposal.WaitAsync(Patience).ConfigureAwait(false);
		await run.WaitAsync(Patience).ConfigureAwait(false);

		stoppedBeforeTheOutputWasRead.Should().BeFalse("the output must be sent before the peer's input stops being read");
		received.Should().BeGreaterThanOrEqualTo(1024);
		input.CancelRequested.Should().BeTrue();
	}

	[TestMethod]
	[Description("Verifies disposing a framing whose run has already ended leaves the caller's pipe as it was, rather than cancelling the caller's next read.")]
	public async Task When_AFramingWhoseRunEndedIsDisposed_Then_TheCallersNextReadIsNotCancelled()
	{
		var fromPeer = new Pipe();
		var toPeer = new Pipe();
		var pipe = new DuplexPipe(fromPeer.Reader, toPeer.Writer);
		var framing = new TelnetFraming(pipe);
		using var caller = new CancellationTokenSource();
		var run = framing.RunAsync(caller.Token);
		await ReadAtLeastAsync(toPeer.Reader, 1).WaitAsync(Patience).ConfigureAwait(false);
		await caller.CancelAsync().ConfigureAwait(false);
		await run.WaitAsync(Patience).ConfigureAwait(false);

		await framing.DisposeAsync().ConfigureAwait(false);

		var read = pipe.Input.ReadAsync().AsTask();
		await fromPeer.Writer.WriteAsync("x"u8.ToArray()).ConfigureAwait(false);
		(await read.WaitAsync(Patience).ConfigureAwait(false)).IsCanceled.Should().BeFalse();
	}

	private sealed record DuplexPipe(PipeReader Input, PipeWriter Output) : IDuplexPipe;

	// Reads what the framing sent until at least the given number of bytes arrived, and reports how many did.
	private static async Task<long> ReadAtLeastAsync(PipeReader reader, long count)
	{
		long received = 0;
		while (received < count)
		{
			var result = await reader.ReadAsync().ConfigureAwait(false);
			received += result.Buffer.Length;
			reader.AdvanceTo(result.Buffer.End);
			if (result.IsCompleted)
			{
				break;
			}
		}

		return received;
	}

	// A pipe reader that records whether its pending read was cancelled.
	private sealed class RecordingPipeReader(PipeReader inner) : PipeReader
	{
		private volatile bool _cancelRequested;

		public bool CancelRequested => _cancelRequested;

		public override void AdvanceTo(SequencePosition consumed) => inner.AdvanceTo(consumed);

		public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => inner.AdvanceTo(consumed, examined);

		public override void CancelPendingRead()
		{
			_cancelRequested = true;
			inner.CancelPendingRead();
		}

		public override void Complete(Exception? exception = null) => inner.Complete(exception);

		public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);

		public override bool TryRead(out ReadResult result) => inner.TryRead(out result);
	}
}
