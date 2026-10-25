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

	[TestMethod]
	[Description("Verifies disposing a framing while its opening negotiation is still being written waits for that write, rather than returning while the framing still writes to the caller's pipe.")]
	public async Task When_AFramingIsDisposedDuringItsNegotiation_Then_DisposalWaitsForIt()
	{
		var toPeer = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
		var framing = new TelnetFraming(new DuplexPipe(new Pipe().Reader, toPeer.Writer));
		var run = framing.RunAsync(CancellationToken.None);
		await WaitUntilWrittenAsync(toPeer.Reader).WaitAsync(Patience).ConfigureAwait(false);

		var disposal = framing.DisposeAsync().AsTask();
		var disposedDuringTheNegotiation = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromMilliseconds(200))).ConfigureAwait(false) == disposal;
		toPeer.Reader.TryRead(out var written).Should().BeTrue();
		toPeer.Reader.AdvanceTo(written.Buffer.End);
		await disposal.WaitAsync(Patience).ConfigureAwait(false);
		await run.WaitAsync(Patience).ConfigureAwait(false);

		disposedDuringTheNegotiation.Should().BeFalse("disposal must wait for the negotiation it interrupts");
	}

	[TestMethod]
	[Description("Verifies disposing a framing whose peer stopped reading still ends, once the write it is held on has had its allowance, instead of waiting forever to send it.")]
	public async Task When_ThePeerStopsReading_Then_DisposalStillEnds()
	{
		var toPeer = new Pipe(new PipeOptions(pauseWriterThreshold: 64, resumeWriterThreshold: 32));
		var framing = new TelnetFraming(new DuplexPipe(new Pipe().Reader, toPeer.Writer));
		var run = framing.RunAsync(CancellationToken.None);
		await framing.Output.WriteAsync(new string('x', 1024)).ConfigureAwait(false);

		await framing.DisposeAsync().AsTask().WaitAsync(Patience).ConfigureAwait(false);
		await run.WaitAsync(Patience).ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Verifies disposing a framing whose peer reads slowly but steadily sends all the output, each write being given its own allowance as it starts.")]
	public async Task When_ThePeerReadsSlowlyButSteadily_Then_DisposalSendsAllTheOutput()
	{
		var toPeer = new Pipe(new PipeOptions(pauseWriterThreshold: 64, resumeWriterThreshold: 32));
		var framing = new TelnetFraming(new DuplexPipe(new Pipe().Reader, toPeer.Writer));
		var run = framing.RunAsync(CancellationToken.None);
		for (var i = 0; i < 16; i++)
		{
			await framing.Output.WriteAsync(new string('x', 64)).ConfigureAwait(false);
		}

		var disposal = framing.DisposeAsync().AsTask();
		var received = await ReadAtLeastAsync(toPeer.Reader, 1024, pause: TimeSpan.FromMilliseconds(250)).WaitAsync(Patience).ConfigureAwait(false);
		await disposal.WaitAsync(Patience).ConfigureAwait(false);
		await run.WaitAsync(Patience).ConfigureAwait(false);

		received.Should().BeGreaterThanOrEqualTo(1024);
	}

	[TestMethod]
	[Description("Verifies a disposal that starts while the negotiation is held still gives each later write its own allowance, so a peer that keeps up, if slowly, receives all the output.")]
	public async Task When_DisposalStartsDuringASlowNegotiation_Then_AllTheOutputIsStillSent()
	{
		var toPeer = new Pipe();
		using var output = new SteppedPipeWriter(toPeer.Writer);
		var framing = new TelnetFraming(new DuplexPipe(new Pipe().Reader, output));
		var run = framing.RunAsync(CancellationToken.None);
		await output.Waiting.WaitAsync(Patience).ConfigureAwait(false);
		await framing.Output.WriteAsync("DATA").ConfigureAwait(false);
		await framing.Output.WriteAsync("TAIL").ConfigureAwait(false);

		// The negotiation goes through 1.4 s into the disposal and the data 1 s later: past the two seconds the disposal
		// started with, but within the two seconds the data's write is given as it starts.
		var disposal = framing.DisposeAsync().AsTask();
		await Task.Delay(TimeSpan.FromMilliseconds(1400)).ConfigureAwait(false);
		output.Step();
		await Task.Delay(TimeSpan.FromMilliseconds(1000)).ConfigureAwait(false);
		output.Open();
		await disposal.WaitAsync(Patience).ConfigureAwait(false);
		await run.WaitAsync(Patience).ConfigureAwait(false);

		toPeer.Reader.TryRead(out var sent).Should().BeTrue();
		System.Text.Encoding.ASCII.GetString(System.Buffers.BuffersExtensions.ToArray(sent.Buffer)).Should().EndWith("DATATAIL");
	}

	// A pipe writer whose flushes wait for the test to let them through, one at a time or all at once.
	private sealed class SteppedPipeWriter(PipeWriter inner) : PipeWriter, IDisposable
	{
		private readonly SemaphoreSlim _steps = new(0);
		private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private volatile bool _open;

		public Task Waiting => _waiting.Task;

		public void Step() => _steps.Release();

		public void Open()
		{
			_open = true;
			_steps.Release();
		}

		public override void Advance(int bytes) => inner.Advance(bytes);

		public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

		public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

		public override void CancelPendingFlush() => inner.CancelPendingFlush();

		public override void Complete(Exception? exception = null) => inner.Complete(exception);

		public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
		{
			_waiting.TrySetResult();
			if (!_open)
			{
				await _steps.WaitAsync(cancellationToken).ConfigureAwait(false);
			}

			return await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
		}

		public void Dispose() => _steps.Dispose();
	}

	[TestMethod]
	[DataRow(1024, 250)]
	[DataRow(512, 300)]
	[Description("Verifies disposing a framing sends all of one large output to a peer that reads it slowly but steadily, above the minimum pace, even one that takes much less than a write slice at a time.")]
	public async Task When_ThePeerReadsALargeOutputSlowlyButSteadily_Then_DisposalSendsAllOfIt(int take, int pauseMilliseconds)
	{
		var toPeer = new Pipe(new PipeOptions(pauseWriterThreshold: 1024, resumeWriterThreshold: 512));
		var framing = new TelnetFraming(new DuplexPipe(new Pipe().Reader, toPeer.Writer));
		var run = framing.RunAsync(CancellationToken.None);
		await framing.Output.WriteAsync(new string('x', 16 * 1024)).ConfigureAwait(false);
		await framing.Output.WriteAsync("TAIL").ConfigureAwait(false);
		const int Negotiation = 21;
		var everything = Negotiation + (16 * 1024) + "TAIL".Length;

		var disposal = framing.DisposeAsync().AsTask();
		var reading = ReadAtLeastAsync(
			toPeer.Reader, everything, pause: TimeSpan.FromMilliseconds(pauseMilliseconds), take: take);
		await disposal.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
		await run.WaitAsync(Patience).ConfigureAwait(false);

		// Disposal gave up on the output if it ended before everything was written: the tail never arrives, and the
		// read times out.
		await reading.WaitAsync(Patience).ConfigureAwait(false);
	}

	[TestMethod]
	[Description("Verifies a full write slice already held when disposal begins is given the time it takes at the minimum pace, well past two seconds, so the output after it still goes out.")]
	public async Task When_AFullSliceIsHeldAsDisposalBegins_Then_ItIsGivenTheTimeItTakesAtTheMinimumPace()
	{
		var toPeer = new Pipe(new PipeOptions(pauseWriterThreshold: 1024, resumeWriterThreshold: 512));
		var framing = new TelnetFraming(new DuplexPipe(new Pipe().Reader, toPeer.Writer));
		var run = framing.RunAsync(CancellationToken.None);
		await framing.Output.WriteAsync(new string('x', 4096)).ConfigureAwait(false);
		await framing.Output.WriteAsync("TAIL").ConfigureAwait(false);
		const int Negotiation = 21;
		var slice = Negotiation + 4096;
		await WaitUntilWrittenAsync(toPeer.Reader, slice).WaitAsync(Patience).ConfigureAwait(false);

		// The slice waits on the unread pipe as disposal begins. It is given 16 s at 256 bytes a second; six seconds is
		// past the two a short write gets, and past what a pace above about 680 bytes a second would give it.
		var disposal = framing.DisposeAsync().AsTask();
		await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
		var received = await ReadAtLeastAsync(toPeer.Reader, slice + "TAIL".Length).WaitAsync(Patience).ConfigureAwait(false);
		await disposal.WaitAsync(Patience).ConfigureAwait(false);
		await run.WaitAsync(Patience).ConfigureAwait(false);

		received.Should().Be(slice + "TAIL".Length);
	}

	[TestMethod]
	[DataRow(4095, "é", 4095)]
	[DataRow(4094, "€", 4094)]
	[DataRow(4095, "€", 4095)]
	[DataRow(4093, "\U0001F600", 4093)]
	[DataRow(4094, "\U0001F600", 4094)]
	[DataRow(4095, "\U0001F600", 4095)]
	[DataRow(4096, "é", 4096)]
	[Description("Verifies the output is cut into writes on character boundaries, so a consumer that decodes each write on its own, such as one reading WebSocket messages, never sees a character split in two, whatever its width and wherever the slice size falls in it.")]
	public async Task When_ACharacterStraddlesAWriteSlice_Then_EachWriteHoldsWholeCharacters(
		int prefix, string character, int firstWrite)
	{
		var toPeer = new Pipe();
		var output = new FlushRecordingPipeWriter(toPeer.Writer);
		var framing = new TelnetFraming(new DuplexPipe(new Pipe().Reader, output));
		var run = framing.RunAsync(CancellationToken.None);
		var text = new string('a', prefix) + character + "TAIL";
		await framing.Output.WriteAsync(text).ConfigureAwait(false);
		await framing.DisposeAsync().AsTask().WaitAsync(Patience).ConfigureAwait(false);
		await run.WaitAsync(Patience).ConfigureAwait(false);

		toPeer.Reader.TryRead(out var sent).Should().BeTrue();
		var bytes = System.Buffers.BuffersExtensions.ToArray(sent.Buffer);
		var strict = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
		var decoded = new System.Text.StringBuilder();
		// The first write is the negotiation, which is not text.
		var offset = (int)output.Flushes[0];
		foreach (var length in output.Flushes.Skip(1))
		{
			var write = () => strict.GetString(bytes, offset, (int)length);
			decoded.Append(write.Should().NotThrow("a write must not end inside a character").Subject);
			offset += (int)length;
		}

		decoded.ToString().Should().Be(text);
		output.Flushes.Skip(1).Should().HaveCount(2).And.AllSatisfy(length => length.Should().BeLessThanOrEqualTo(4096));
		output.Flushes[1].Should().Be(firstWrite, "the first write ends just before the character the slice size falls in");
	}

	// A pipe writer that records how many bytes each flush sends.
	private sealed class FlushRecordingPipeWriter(PipeWriter inner) : PipeWriter
	{
		private readonly List<long> _flushes = [];
		private long _pending;

		public IReadOnlyList<long> Flushes
		{
			get
			{
				lock (_flushes)
				{
					return [.. _flushes];
				}
			}
		}

		public override void Advance(int bytes)
		{
			_pending += bytes;
			inner.Advance(bytes);
		}

		public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

		public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

		public override void CancelPendingFlush() => inner.CancelPendingFlush();

		public override void Complete(Exception? exception = null) => inner.Complete(exception);

		public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
		{
			lock (_flushes)
			{
				_flushes.Add(_pending);
			}

			_pending = 0;
			return inner.FlushAsync(cancellationToken);
		}
	}

	private sealed record DuplexPipe(PipeReader Input, PipeWriter Output) : IDuplexPipe;

	// Waits until the framing has written at least the given number of bytes, without consuming them.
	private static async Task WaitUntilWrittenAsync(PipeReader reader, long count = 1)
	{
		while (true)
		{
			var result = await reader.ReadAsync().ConfigureAwait(false);
			if (result.Buffer.Length >= count || result.IsCompleted)
			{
				reader.AdvanceTo(result.Buffer.Start);
				return;
			}

			reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
		}
	}

	// Reads what the framing sent until at least the given number of bytes arrived, and reports how many did. A slow
	// peer pauses before each read and takes at most a few bytes each time.
	private static async Task<long> ReadAtLeastAsync(
		PipeReader reader, long count, TimeSpan pause = default, long take = long.MaxValue)
	{
		long received = 0;
		while (received < count)
		{
			if (pause > TimeSpan.Zero)
			{
				await Task.Delay(pause).ConfigureAwait(false);
			}

			var result = await reader.ReadAsync().ConfigureAwait(false);
			var taken = Math.Min(result.Buffer.Length, take);
			received += taken;
			var consumed = result.Buffer.GetPosition(taken);
			reader.AdvanceTo(consumed, consumed);
			if (result.IsCompleted && taken == result.Buffer.Length)
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
