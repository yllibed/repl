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
	private readonly Channel<byte[]> _outChannel = Channel.CreateUnbounded<byte[]>();

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
	/// Sends the initial Telnet negotiation and runs the receive/send loops
	/// until the transport closes or cancellation is requested.
	/// </summary>
	public async Task RunAsync(CancellationToken ct)
	{
		await SendNegotiationAsync(ct).ConfigureAwait(false);

		var sendTask = SendLoopAsync(ct);
		try
		{
			await ReceiveLoopAsync(ct).ConfigureAwait(false);
		}
		finally
		{
			_outChannel.Writer.TryComplete();
			_vtInput.Complete();
			try { await sendTask.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
	}

	/// <inheritdoc />
	public ValueTask DisposeAsync()
	{
		_outChannel.Writer.TryComplete();
		_vtInput.Complete();
		return default;
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

		await _pipe.Output.WriteAsync(negotiation, ct).ConfigureAwait(false);
	}

	private async Task ReceiveLoopAsync(CancellationToken ct)
	{
		var reader = _pipe.Input;
		try
		{
			while (!ct.IsCancellationRequested)
			{
				var result = await reader.ReadAsync(ct).ConfigureAwait(false);
				var buffer = result.Buffer;

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
	}

	private async Task SendLoopAsync(CancellationToken ct)
	{
		try
		{
			await foreach (var data in _outChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
			{
				await _pipe.Output.WriteAsync(data, ct).ConfigureAwait(false);
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
