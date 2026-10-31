using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;

using Repl;

namespace Repl.Telnet;

/// <summary>
/// Transport-agnostic Telnet framing layer.
/// Separates IAC command sequences from the VT data stream and handles
/// option negotiation (BINARY, SGA, ECHO, NAWS, TERMINAL-TYPE).
/// Works over any <see cref="IDuplexPipe"/> (TCP, WebSocket, named pipe, etc.).
/// </summary>
public sealed class TelnetFraming : IAsyncDisposable
{
	private readonly IDuplexPipe _pipe;
	private readonly ChannelTextReader _vtInput = new();
	private readonly TelnetTextWriter _vtOutput;
	// The most the send loop writes at once, so a long output is given time write by write, and a peer that stopped
	// reading holds disposal no longer than one write's allowance.
	private const int MaxWriteSlice = 4096;

	// The most continuation bytes a UTF-8 character has, which bounds how far a slice backs off to end between two.
	private const int MaxUtf8ContinuationBytes = 3;

	// Once the session has ended, a write is given up when the peer has not taken it in the time it needs at this pace,
	// in bytes a second, or in MinWriteAllowance for a short one; the remarks on DisposeAsync and the change note in
	// docs/execution-pipeline.md state it too. A transport reports no progress inside a write, so the allowance is
	// sized to the write rather than renewed as the peer reads it.
	private const int MinDrainRate = 256;

	private static readonly TimeSpan MinWriteAllowance = TimeSpan.FromSeconds(2);

	private readonly Channel<byte[]> _outChannel = Channel.CreateUnbounded<byte[]>();
	// Guards what RunAsync starts against disposal: the sending, the read, and whether the framing has stopped.
	private readonly Lock _gate = new();
	private Task _sending = Task.CompletedTask;
	private CancellationTokenSource? _sendingStop;
	private bool _draining;
	// The length of the write under way, 0 when there is none, so a disposal that begins during it allows it in full.
	private int _writeLength;
	private bool _reading;
	private bool _stopped;

	private ParserState _state = ParserState.Normal;
	private byte _negotiationCommand;
	private readonly List<byte> _subnegBuffer = new(64);

	/// <summary>
	/// Creates a new Telnet framing layer over a bidirectional pipe.
	/// </summary>
	public TelnetFraming(IDuplexPipe pipe)
	{
		ArgumentNullException.ThrowIfNull(pipe);
		_pipe = pipe;
		_vtOutput = new TelnetTextWriter(_outChannel);
	}

	/// <summary>VT data reader (IAC stripped) — connect to <see cref="StreamedReplHost"/>.</summary>
	public ChannelTextReader Input => _vtInput;

	/// <summary>VT data writer (auto-escapes 0xFF) — connect to <see cref="StreamedReplHost"/>.</summary>
	public TextWriter Output => _vtOutput;

	/// <summary>Terminal type reported by the client, if any.</summary>
	public string? TerminalType { get; private set; }

	/// <summary>Raised when the client sends a NAWS subnegotiation with a new window size.</summary>
	public event EventHandler<WindowSizeEventArgs>? WindowSizeChanged;

	/// <summary>Raised when the client reports a terminal type through TERMINAL-TYPE negotiation.</summary>
	public event EventHandler<TerminalTypeEventArgs>? TerminalTypeChanged;

	/// <summary>
	/// Sends the initial Telnet negotiation and runs the receive/send loops. It returns once the peer's input ends,
	/// cancellation is requested, or the framing is disposed; the output stays open until the framing is disposed, so
	/// the reply to a command the peer sent before closing its side still goes out.
	/// </summary>
	public async Task RunAsync(CancellationToken ct)
	{
		CancellationTokenSource sendingStop;
		lock (_gate)
		{
			// Disposal marks the output as draining before it looks for the sending to wait for, so a run that starts
			// once disposal began would write what disposal no longer waits for.
			if (_stopped || _draining)
			{
				return;
			}

			// Started under the gate, so a disposal racing this start still waits for the negotiation.
			sendingStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
			_sendingStop = sendingStop;
			_sending = SendAsync(sendingStop);
		}

		try
		{
			await ReceiveLoopAsync(ct).ConfigureAwait(false);
		}
		finally
		{
			// The session sees the end of the peer's input; its output stays open for what it still has to send.
			_vtInput.Complete();
		}
	}

	/// <inheritdoc />
	/// <remarks>
	/// Waits for the output already written to be sent, then cancels the read <see cref="RunAsync"/> has pending on
	/// the pipe, if any, so that it returns; the pipe is not completed. It also waits for an opening negotiation still
	/// being written. The output goes out in writes of at most 4 KB, each given two seconds or the time it takes at 256
	/// bytes a second, whichever is longer, counted from its start, or from the start of disposal for one already under
	/// way; the rest is given up when one is not done by then. A peer that stopped reading thus holds disposal about
	/// sixteen seconds at most, while one that keeps that pace receives all the output however long it takes; cancel
	/// the token passed to <see cref="RunAsync"/> to bound that. A write is done once the transport accepts it: over a
	/// pipe that buffers, such as a <see cref="Pipe"/>, the peer must also read what the pipe holds past its resume
	/// threshold in that time. How a cancelled read ends depends on the transport: <see cref="WebSocketStream"/>
	/// closes its WebSocket. Disposal is what ends the output once <see cref="RunAsync"/> has returned, so a framing must be
	/// disposed, and before its pipe is completed.
	/// </remarks>
	public async ValueTask DisposeAsync()
	{
		// The output goes out first, as stopping a read can end the connection on some transports.
		try
		{
			await EndOutputAsync().ConfigureAwait(false);
		}
		finally
		{
			StopReading();
		}
	}

	// Ends the output and the VT input, then waits for what was already written to be sent.
	private async Task EndOutputAsync()
	{
		_outChannel.Writer.TryComplete();
		_vtInput.Complete();
		Task sending;
		lock (_gate)
		{
			_draining = true;
			_sendingStop?.CancelAfter(WriteAllowance(_writeLength));
			sending = _sending;
		}

#pragma warning disable VSTHRD003 // The sending RunAsync started is what ending the output waits to drain.
		await sending.ConfigureAwait(false);
#pragma warning restore VSTHRD003
	}

	// Stops reading the peer: a later read will not start, and the one RunAsync has pending returns.
	private void StopReading()
	{
		lock (_gate)
		{
			_stopped = true;
			if (_reading)
			{
				_pipe.Input.CancelPendingRead();
			}
		}
	}

	// Owns its stop source, which outlives RunAsync when the peer's input ends first.
	private async Task SendAsync(CancellationTokenSource stop)
	{
		var ct = stop.Token;
		try
		{
			// Leaves the gate RunAsync started this under before any write, which may wait for the peer.
			await Task.Yield();
			try
			{
				await SendNegotiationAsync(ct).ConfigureAwait(false);
			}
			catch (IOException)
			{
				// The peer left before the negotiation could be sent: the session has no one to serve.
				_vtInput.Complete();
				StopReading();
				return;
			}
			catch (OperationCanceledException)
			{
				return;
			}

			await SendLoopAsync(ct).ConfigureAwait(false);
		}
		finally
		{
			lock (_gate)
			{
				_sendingStop = null;
			}

			stop.Dispose();
		}
	}

	private async Task SendNegotiationAsync(CancellationToken ct)
	{
		// TERMINAL-TYPE negotiation is placed before NAWS so that the client's
		// TERMINAL-TYPE response arrives before NAWS, allowing the terminal type
		// to be resolved before the NAWS-based window size provider unblocks.
		byte[] negotiation =
		[
			TelnetCommand.Iac, TelnetCommand.Will, TelnetOption.Echo,
			TelnetCommand.Iac, TelnetCommand.Will, TelnetOption.Sga,
			TelnetCommand.Iac, TelnetCommand.Do, TelnetOption.Binary,
			TelnetCommand.Iac, TelnetCommand.Do, TelnetOption.TerminalType,
			TelnetCommand.Iac, TelnetCommand.SB, TelnetOption.TerminalType, 1,
			TelnetCommand.Iac, TelnetCommand.SE,
			TelnetCommand.Iac, TelnetCommand.Do, TelnetOption.Naws,
		];

		await WriteAsync(negotiation, ct).ConfigureAwait(false);
	}

	private async Task ReceiveLoopAsync(CancellationToken ct)
	{
		// Disposal cancels a read only while this loop runs, so a caller's later read on its pipe is not cancelled,
		// short of a disposal that lands as the loop ends.
		lock (_gate)
		{
			if (_stopped)
			{
				return;
			}

			_reading = true;
		}

		var reader = _pipe.Input;
		try
		{
			while (!ct.IsCancellationRequested)
			{
				var result = await reader.ReadAsync(ct).ConfigureAwait(false);
				var buffer = result.Buffer;
				if (result.IsCanceled)
				{
					reader.AdvanceTo(buffer.Start);
					break;
				}

				foreach (var segment in buffer)
				{
					ProcessIncoming(segment.Span);
				}

				reader.AdvanceTo(buffer.End);

				if (result.IsCompleted) break;
			}
		}
		catch (OperationCanceledException) { }
		catch (IOException) { }
		finally
		{
			lock (_gate)
			{
				_reading = false;
			}
		}
	}

	// Writes to the peer; while draining, the write gets its own allowance as it starts.
	private async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
	{
		lock (_gate)
		{
			_writeLength = data.Length;
			if (_draining)
			{
				_sendingStop?.CancelAfter(WriteAllowance(data.Length));
			}
		}

		try
		{
			await _pipe.Output.WriteAsync(data, ct).ConfigureAwait(false);
		}
		finally
		{
			lock (_gate)
			{
				_writeLength = 0;
			}
		}
	}

	// Ends a slice on a character boundary, so a consumer that decodes each write on its own, such as one reading
	// WebSocket messages, never sees a character split in two. The output is UTF-8, which never holds the 0xFF that IAC
	// escaping doubles, so a cut between characters never splits an escape either.
	private static int SliceLength(byte[] data, int offset)
	{
		var end = offset + MaxWriteSlice;
		if (end >= data.Length)
		{
			return data.Length - offset;
		}

		// Bytes that are not UTF-8 are cut at the slice size.
		for (var cut = end; cut > end - MaxUtf8ContinuationBytes - 1; cut--)
		{
			if (!IsUtf8Continuation(data[cut]))
			{
				return cut - offset;
			}
		}

		return MaxWriteSlice;
	}

	private static bool IsUtf8Continuation(byte value) => (value & 0xC0) == 0x80;

	private static TimeSpan WriteAllowance(int length)
	{
		var atMinDrainRate = TimeSpan.FromSeconds((double)length / MinDrainRate);
		return atMinDrainRate > MinWriteAllowance ? atMinDrainRate : MinWriteAllowance;
	}

	private async Task SendLoopAsync(CancellationToken ct)
	{
		try
		{
			await foreach (var data in _outChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
			{
				for (var offset = 0; offset < data.Length;)
				{
					var length = SliceLength(data, offset);
					await WriteAsync(data.AsMemory(offset, length), ct).ConfigureAwait(false);
					offset += length;
				}
			}
		}
		catch (OperationCanceledException) { }
		catch (IOException) { }
	}

	private void ProcessIncoming(ReadOnlySpan<byte> data)
	{
		var vtStart = -1;

		for (var i = 0; i < data.Length; i++)
		{
			var b = data[i];
			if (_state == ParserState.Normal)
			{
				if (b == TelnetCommand.Iac)
				{
					if (vtStart >= 0) { FlushVt(data[vtStart..i]); vtStart = -1; }
					_state = ParserState.Iac;
				}
				else if (vtStart < 0)
				{
					vtStart = i;
				}

				continue;
			}

			ProcessIacByte(b);
		}

		if (vtStart >= 0) FlushVt(data[vtStart..]);
	}

	private void ProcessIacByte(byte b)
	{
		switch (_state)
		{
			case ParserState.Iac:
				ProcessIacCommand(b);
				break;
			case ParserState.Negotiation:
				HandleNegotiation(_negotiationCommand, b);
				_state = ParserState.Normal;
				break;
			case ParserState.Subnegotiation:
				if (b == TelnetCommand.Iac) _state = ParserState.SubnegIac;
				else _subnegBuffer.Add(b);
				break;
			case ParserState.SubnegIac:
				ProcessSubnegIac(b);
				break;
		}
	}

	private void ProcessIacCommand(byte b)
	{
		if (b == TelnetCommand.Iac)
		{
			_vtInput.Enqueue("\xFF");
			_state = ParserState.Normal;
		}
		else if (b is TelnetCommand.Will or TelnetCommand.Wont
		         or TelnetCommand.Do or TelnetCommand.Dont)
		{
			_negotiationCommand = b;
			_state = ParserState.Negotiation;
		}
		else if (b == TelnetCommand.SB)
		{
			_subnegBuffer.Clear();
			_state = ParserState.Subnegotiation;
		}
		else
		{
			_state = ParserState.Normal;
		}
	}

	private void ProcessSubnegIac(byte b)
	{
		if (b == TelnetCommand.SE)
		{
			HandleSubnegotiation();
			_state = ParserState.Normal;
		}
		else if (b == TelnetCommand.Iac)
		{
			_subnegBuffer.Add(TelnetCommand.Iac);
			_state = ParserState.Subnegotiation;
		}
		else
		{
			_state = ParserState.Normal;
		}
	}

	private void FlushVt(ReadOnlySpan<byte> vtData)
	{
		if (vtData.Length == 0) return;
		_vtInput.Enqueue(Encoding.UTF8.GetString(vtData));
	}

	private void HandleNegotiation(byte command, byte option)
	{
		switch (command)
		{
			case TelnetCommand.Will:
				var doResponse = option is TelnetOption.Binary or TelnetOption.Naws or TelnetOption.TerminalType
					? TelnetCommand.Do
					: TelnetCommand.Dont;
				_outChannel.Writer.TryWrite([TelnetCommand.Iac, doResponse, option]);
				break;
			case TelnetCommand.Do:
				var willResponse = option is TelnetOption.Echo or TelnetOption.Sga
					? TelnetCommand.Will
					: TelnetCommand.Wont;
				_outChannel.Writer.TryWrite([TelnetCommand.Iac, willResponse, option]);
				break;
		}
	}

	private void HandleSubnegotiation()
	{
		if (_subnegBuffer.Count == 0) return;
		var option = _subnegBuffer[0];

		switch (option)
		{
			case TelnetOption.Naws when _subnegBuffer.Count >= 5:
				var width = (_subnegBuffer[1] << 8) | _subnegBuffer[2];
				var height = (_subnegBuffer[3] << 8) | _subnegBuffer[4];
				if (width > 0 && height > 0)
				{
					WindowSizeChanged?.Invoke(this, new WindowSizeEventArgs(width, height));
				}

				break;
			case TelnetOption.TerminalType when _subnegBuffer.Count >= 2 && _subnegBuffer[1] == 0:
				if (_subnegBuffer.Count > 2)
				{
					TerminalType = Encoding.ASCII.GetString(
						[.. _subnegBuffer.Skip(2)]);
					TerminalTypeChanged?.Invoke(this, new TerminalTypeEventArgs(TerminalType));
				}

				break;
		}
	}

	/// <summary>
	/// Event payload carrying a terminal identity reported by Telnet TERMINAL-TYPE.
	/// </summary>
	public sealed class TerminalTypeEventArgs(string terminalType) : EventArgs
	{
		/// <summary>
		/// Gets the reported terminal type.
		/// </summary>
		public string TerminalType { get; } = terminalType;
	}

	private enum ParserState
	{
		Normal,
		Iac,
		Negotiation,
		Subnegotiation,
		SubnegIac,
	}

	/// <summary>
	/// TextWriter that encodes text to UTF-8 and escapes 0xFF for Telnet framing.
	/// </summary>
	private sealed class TelnetTextWriter(Channel<byte[]> outChannel) : TextWriter
	{
		public override Encoding Encoding => Encoding.UTF8;

		public override void Write(char value) =>
			Write(new ReadOnlySpan<char>(in value));

		public override void Write(string? value)
		{
			if (value is null) return;
			Write(value.AsSpan());
		}

		public override void Write(ReadOnlySpan<char> buffer)
		{
			if (buffer.IsEmpty) return;
			var bytes = Encoding.UTF8.GetBytes(buffer.ToArray());
			outChannel.Writer.TryWrite(EscapeIac(bytes));
		}

		public override void WriteLine(string? value)
		{
			Write(value);
			Write(Environment.NewLine);
		}

		public override Task WriteAsync(string? value)
		{
			Write(value);
			return Task.CompletedTask;
		}

		public override Task WriteLineAsync(string? value)
		{
			WriteLine(value);
			return Task.CompletedTask;
		}

		public override Task FlushAsync() => Task.CompletedTask;

		public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		private static byte[] EscapeIac(byte[] data)
		{
			if (!data.AsSpan().Contains((byte)0xFF))
			{
				return data;
			}

			var count = 0;
			foreach (var b in data)
			{
				if (b == 0xFF) count++;
			}

			var escaped = new byte[data.Length + count];
			var j = 0;
			foreach (var b in data)
			{
				escaped[j++] = b;
				if (b == 0xFF) escaped[j++] = 0xFF;
			}

			return escaped;
		}
	}
}
