namespace Repl;

/// <summary>
/// An <see cref="IReplKeyReader"/> that reads from a <see cref="TextReader"/> and parses
/// VT/ANSI escape sequences into <see cref="ConsoleKeyInfo"/> values.
/// Used for server-side line editing over remote transports (WebSocket, SignalR, etc.).
/// </summary>
public sealed class VtKeyReader : IReplKeyReader
{
	private readonly TextReader _reader;
	private readonly char[] _charBuf = new char[1];

	/// <summary>
	/// Initializes a new instance of the <see cref="VtKeyReader"/> class.
	/// </summary>
	/// <param name="reader">Underlying character reader (typically a <see cref="ChannelTextReader"/>).</param>
	public VtKeyReader(TextReader reader)
	{
		ArgumentNullException.ThrowIfNull(reader);
		_reader = reader;
	}

	/// <summary>
	/// Optional callback invoked when a DTTERM window size report is received.
	/// Parameters are (cols, rows).
	/// </summary>
	public Action<int, int>? OnResize { get; set; }

	/// <inheritdoc />
	public bool KeyAvailable => false;

	/// <inheritdoc />
	public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct)
	{
		var ch = await ReadCharAsync(ct).ConfigureAwait(false);

		return ch switch
		{
			'\x1b' => await ParseEscapeAsync(ct).ConfigureAwait(false),
			'\r' or '\n' => MakeKey(ConsoleKey.Enter, '\r'),
			'\t' => MakeKey(ConsoleKey.Tab, '\t'),
			'\x7f' => MakeKey(ConsoleKey.Backspace, '\b'),
			'\b' => MakeKey(ConsoleKey.Backspace, '\b'),
			_ => MakeCharKey(ch),
		};
	}

	private async ValueTask<ConsoleKeyInfo> ParseEscapeAsync(CancellationToken ct)
	{
		// Try to read the next char with a short timeout to distinguish
		// standalone Escape from the start of an escape sequence.
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(50);

		char next;
		try
		{
			next = await ReadCharAsync(cts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			return MakeKey(ConsoleKey.Escape, '\x1b');
		}

		return next switch
		{
			'[' => await ParseCsiAsync(ct).ConfigureAwait(false),
			'O' => await ParseSs3Async(ct).ConfigureAwait(false),
			_ => MakeKey(ConsoleKey.Escape, '\x1b'), // Unknown, treat as Escape
		};
	}

	private async ValueTask<ConsoleKeyInfo> ParseCsiAsync(CancellationToken ct)
	{
		// CSI sequences: \x1b[ followed by optional numeric params and a final letter/tilde.
		// Track up to 3 params to support window size reports (\x1b[8;rows;colst).
		int p0 = 0, p1 = 0, p2 = 0;
		var pi = 0;

		while (true)
		{
			var ch = await ReadCharAsync(ct).ConfigureAwait(false);
			if (ch is >= '0' and <= '9')
			{
				var digit = ch - '0';
				switch (pi)
				{
					case 0: p0 = p0 * 10 + digit; break;
					case 1: p1 = p1 * 10 + digit; break;
					default: p2 = p2 * 10 + digit; break;
				}

				continue;
			}

			if (ch == ';')
			{
				pi++;
				continue;
			}

			// Final character.
			return ch switch
			{
				'A' => MakeKey(ConsoleKey.UpArrow, default),
				'B' => MakeKey(ConsoleKey.DownArrow, default),
				'C' => MakeKey(ConsoleKey.RightArrow, default),
				'D' => MakeKey(ConsoleKey.LeftArrow, default),
				'H' => MakeKey(ConsoleKey.Home, default),
				'F' => MakeKey(ConsoleKey.End, default),
				't' when p0 == 8 && pi >= 2 => HandleResize(p1, p2),
				'~' => p0 switch
				{
					1 => MakeKey(ConsoleKey.Home, default),
					2 => MakeKey(ConsoleKey.Insert, default),
					3 => MakeKey(ConsoleKey.Delete, default),
					4 => MakeKey(ConsoleKey.End, default),
					5 => MakeKey(ConsoleKey.PageUp, default),
					6 => MakeKey(ConsoleKey.PageDown, default),
					_ => MakeKey(default, default), // Unknown
				},
				_ => MakeKey(default, default), // Unknown CSI sequence
			};
		}
	}

	private ConsoleKeyInfo HandleResize(int rows, int cols)
	{
		if (cols > 0 && rows > 0)
		{
			OnResize?.Invoke(cols, rows);
		}

		// Swallow the resize event — return a no-op key that ConsoleLineReader ignores.
		return MakeKey(default, default);
	}

	private async ValueTask<ConsoleKeyInfo> ParseSs3Async(CancellationToken ct)
	{
		// SS3 sequences: \x1bO followed by a single letter.
		var ch = await ReadCharAsync(ct).ConfigureAwait(false);
		return ch switch
		{
			'H' => MakeKey(ConsoleKey.Home, default),
			'F' => MakeKey(ConsoleKey.End, default),
			'P' => MakeKey(ConsoleKey.F1, default),
			'Q' => MakeKey(ConsoleKey.F2, default),
			'R' => MakeKey(ConsoleKey.F3, default),
			'S' => MakeKey(ConsoleKey.F4, default),
			_ => MakeKey(default, default),
		};
	}

	private async ValueTask<char> ReadCharAsync(CancellationToken ct)
	{
		var read = await _reader.ReadAsync(_charBuf.AsMemory(), ct).ConfigureAwait(false);
		if (read == 0)
		{
			throw new OperationCanceledException(ct);
		}

		return _charBuf[0];
	}

	private static ConsoleKeyInfo MakeKey(ConsoleKey key, char keyChar) =>
		new(keyChar, key, shift: false, alt: false, control: false);

	private static ConsoleKeyInfo MakeCharKey(char ch) =>
		new(ch, default, shift: false, alt: false, control: false);
}
