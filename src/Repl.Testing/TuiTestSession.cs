using System.Text;
using Repl.Rendering;
using XTerm.Buffer;
using XTerm.Options;

namespace Repl.Testing;

/// <summary>
/// Headless TUI test harness that wires a REPL session to an XTerm.NET virtual terminal.
/// Enables Playwright-style testing: send commands, read the rendered screen, assert on content and colors.
/// </summary>
public sealed class TuiTestSession : IAsyncDisposable
{
	private readonly ReplApp _app;
	private readonly int _cols;
	private readonly int _rows;
	private readonly XTerm.Terminal _terminal;
	private readonly StringBuilder _raw = new();
	private readonly List<ScreenFrame> _frames = [];
	private readonly Lock _lock = new();

	private StreamedReplHost? _host;
	private Task<int>? _sessionTask;
	private bool _disposed;

	/// <summary>
	/// Creates a new TUI test session.
	/// </summary>
	/// <param name="app">The configured REPL application.</param>
	/// <param name="cols">Virtual terminal width in columns.</param>
	/// <param name="rows">Virtual terminal height in rows.</param>
	public TuiTestSession(ReplApp app, int cols = 80, int rows = 24)
	{
		ArgumentNullException.ThrowIfNull(app);

		_app = app;
		_cols = cols;
		_rows = rows;
		_terminal = new XTerm.Terminal(new TerminalOptions
		{
			Cols = cols,
			Rows = rows,
			Scrollback = 10000,
		});
	}

	/// <summary>
	/// Gets the raw ANSI output written by the REPL.
	/// </summary>
	public string RawOutput
	{
		get { lock (_lock) { return _raw.ToString(); } }
	}

	/// <summary>
	/// Gets the screen frame snapshots captured after each write.
	/// </summary>
	public IReadOnlyList<ScreenFrame> Frames
	{
		get { lock (_lock) { return [.. _frames]; } }
	}

	// ── Lifecycle ────────────────────────────────────────────────

	/// <summary>
	/// Starts the REPL session on a background task.
	/// </summary>
	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (_host is not null)
		{
			throw new InvalidOperationException("Session is already started.");
		}

		var writer = new FrameCaptureWriter(this);
		_host = new StreamedReplHost(writer, new FixedWindowSizeProvider((_cols, _rows)))
		{
			TransportName = "tui-test",
		};

		var options = new ReplRunOptions
		{
			AnsiSupport = AnsiMode.Always,
			TerminalOverrides = new TerminalSessionOverrides
			{
				AnsiSupported = true,
				WindowSize = (_cols, _rows),
			},
		};

		_sessionTask = Task.Run(
			async () =>
			{
				try
				{
					return await _host.RunSessionAsync(_app, options, cancellationToken)
						.ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					return 0;
				}
			},
			cancellationToken);

		return Task.CompletedTask;
	}

#pragma warning disable VSTHRD003 // We own _sessionTask — it was started by StartAsync via Task.Run

	/// <summary>
	/// Signals EOF to the REPL and awaits its exit code.
	/// </summary>
	public async Task<int> StopAsync()
	{
		if (_host is null || _sessionTask is null)
		{
			return 0;
		}

		_host.Complete();
		return await _sessionTask.ConfigureAwait(false);
	}
#pragma warning restore VSTHRD003

	// ── Input ────────────────────────────────────────────────────

	/// <summary>
	/// Sends a command line followed by a newline.
	/// </summary>
	public void SendLine(string command)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_host?.EnqueueInput(command + "\n");
	}

	/// <summary>
	/// Sends raw text without a trailing newline.
	/// </summary>
	public void SendText(string text)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_host?.EnqueueInput(text);
	}

	/// <summary>
	/// Sends a VT escape sequence (e.g. "\x1b[A" for Up arrow).
	/// </summary>
	public void SendKey(string vtSequence)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_host?.EnqueueInput(vtSequence);
	}

	// ── Screen reading ──────────────────────────────────────────

	/// <summary>
	/// Gets a single line of text from the virtual terminal.
	/// </summary>
	public string GetLine(int row)
	{
		lock (_lock)
		{
			return _terminal.GetLine(row);
		}
	}

	/// <summary>
	/// Gets all visible lines from the virtual terminal.
	/// </summary>
	public IReadOnlyList<string> GetVisibleLines()
	{
		lock (_lock)
		{
			var lines = new string[_rows];
			for (var row = 0; row < _rows; row++)
			{
				lines[row] = _terminal.GetLine(row);
			}

			return lines;
		}
	}

	/// <summary>
	/// Gets all visible text joined by newlines.
	/// </summary>
	public string GetScreenText()
	{
		var lines = GetVisibleLines();
		return string.Join('\n', lines);
	}

	/// <summary>
	/// Gets the content and color attributes of a specific cell.
	/// </summary>
	public CellInfo GetCell(int col, int row)
	{
		lock (_lock)
		{
			var bufferRow = _terminal.Buffer.YDisp + row;

			if (bufferRow < 0 || bufferRow >= _terminal.Buffer.Lines.Length)
			{
				return new CellInfo(Content: " ", FgColor: 0, FgMode: 0, BgColor: 0, BgMode: 0, IsInverse: false);
			}

			var line = _terminal.Buffer.Lines[bufferRow];
			if (line is null || col < 0 || col >= line.Length)
			{
				return new CellInfo(Content: " ", FgColor: 0, FgMode: 0, BgColor: 0, BgMode: 0, IsInverse: false);
			}

			var cell = line[col];
			var content = cell.Content;
			if (string.IsNullOrEmpty(content) || string.Equals(content, "\0", StringComparison.Ordinal))
			{
				content = " ";
			}

			return new CellInfo(
				content,
				cell.Attributes.GetFgColor(),
				cell.Attributes.GetFgColorMode(),
				cell.Attributes.GetBgColor(),
				cell.Attributes.GetBgColorMode(),
				cell.Attributes.IsInverse());
		}
	}

	/// <summary>
	/// Finds the first cell on a given row that is not a space.
	/// Returns null if the entire row is blank.
	/// </summary>
	public CellInfo? FindFirstNonBlankCell(int row)
	{
		lock (_lock)
		{
			for (var col = 0; col < _cols; col++)
			{
				var cell = GetCellUnsafe(col, row);
				if (cell is not null && !string.Equals(cell.Content, " ", StringComparison.Ordinal))
				{
					return cell;
				}
			}

			return null;
		}
	}

	/// <summary>
	/// Finds the row index of the first line containing the specified text.
	/// Returns -1 if not found.
	/// </summary>
	public int FindLineContaining(string text)
	{
		lock (_lock)
		{
			for (var row = 0; row < _rows; row++)
			{
				if (_terminal.GetLine(row).Contains(text, StringComparison.Ordinal))
				{
					return row;
				}
			}

			return -1;
		}
	}

	// ── Waiting ─────────────────────────────────────────────────

	/// <summary>
	/// Waits until the specified text appears anywhere on the visible screen.
	/// </summary>
	/// <param name="text">The text to wait for.</param>
	/// <param name="timeout">Maximum time to wait (default: 10 seconds).</param>
	public async Task WaitForTextAsync(string text, TimeSpan? timeout = null)
	{
		var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

		while (DateTime.UtcNow < deadline)
		{
			if (GetScreenText().Contains(text, StringComparison.Ordinal))
			{
				return;
			}

			await Task.Delay(50).ConfigureAwait(false);
		}

		throw new TimeoutException(
			$"Text \"{text}\" did not appear within {(timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds}s.\n" +
			$"Screen content:\n{GetScreenText()}");
	}

	/// <summary>
	/// Waits until no new output has been written for the specified period.
	/// </summary>
	/// <param name="quietPeriod">How long the output must be silent (default: 200ms).</param>
	/// <param name="timeout">Maximum time to wait (default: 10 seconds).</param>
	public async Task WaitForIdleAsync(TimeSpan? quietPeriod = null, TimeSpan? timeout = null)
	{
		var quiet = quietPeriod ?? TimeSpan.FromMilliseconds(200);
		var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

		var lastFrameCount = 0;
		var stableAt = DateTime.UtcNow;

		while (DateTime.UtcNow < deadline)
		{
			int currentCount;
			lock (_lock)
			{
				currentCount = _frames.Count;
			}

			if (currentCount != lastFrameCount)
			{
				lastFrameCount = currentCount;
				stableAt = DateTime.UtcNow;
			}
			else if (DateTime.UtcNow - stableAt >= quiet)
			{
				return;
			}

			await Task.Delay(50).ConfigureAwait(false);
		}

		throw new TimeoutException(
			$"Output did not stabilize within {(timeout ?? TimeSpan.FromSeconds(10)).TotalSeconds}s.");
	}

	// ── Dispose ─────────────────────────────────────────────────

	/// <inheritdoc />
#pragma warning disable VSTHRD003 // We own _sessionTask — it was started by StartAsync via Task.Run
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;

		if (_host is not null)
		{
			_host.Complete();
			if (_sessionTask is not null)
			{
				try
				{
					await _sessionTask.ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					// Expected during teardown.
				}
			}

			await _host.DisposeAsync().ConfigureAwait(false);
		}
	}
#pragma warning restore VSTHRD003

	// ── Internals ───────────────────────────────────────────────

	private CellInfo? GetCellUnsafe(int col, int row)
	{
		var bufferRow = _terminal.Buffer.YDisp + row;

		if (bufferRow < 0 || bufferRow >= _terminal.Buffer.Lines.Length)
		{
			return null;
		}

		var line = _terminal.Buffer.Lines[bufferRow];
		if (line is null || col < 0 || col >= line.Length)
		{
			return null;
		}

		var cell = line[col];
		var content = cell.Content;
		if (string.IsNullOrEmpty(content) || string.Equals(content, "\0", StringComparison.Ordinal))
		{
			content = " ";
		}

		return new CellInfo(
			content,
			cell.Attributes.GetFgColor(),
			cell.Attributes.GetFgColorMode(),
			cell.Attributes.GetBgColor(),
			cell.Attributes.GetBgColorMode(),
			cell.Attributes.IsInverse());
	}

	private void OnTerminalWrite(string text)
	{
		lock (_lock)
		{
			_raw.Append(text);
			_terminal.Write(text);

			var lines = new string[_rows];
			for (var row = 0; row < _rows; row++)
			{
				lines[row] = _terminal.GetLine(row);
			}

			_frames.Add(new ScreenFrame(lines, _terminal.Buffer.X, _terminal.Buffer.Y));
		}
	}

	private sealed class FixedWindowSizeProvider((int Width, int Height) size) : IWindowSizeProvider
	{
		public event EventHandler<WindowSizeEventArgs>? SizeChanged;

		public ValueTask<(int Width, int Height)?> GetSizeAsync(CancellationToken cancellationToken) =>
			ValueTask.FromResult<(int Width, int Height)?>(size);

		// Suppress unused event warning — required by interface.
		internal void OnSizeChanged() => SizeChanged?.Invoke(this, new WindowSizeEventArgs(size.Width, size.Height));
	}

	private sealed class FrameCaptureWriter(TuiTestSession owner) : TextWriter
	{
		public override Encoding Encoding => Encoding.UTF8;

		public override void Write(string? value)
		{
			if (!string.IsNullOrEmpty(value))
			{
				owner.OnTerminalWrite(value);
			}
		}

		public override void Write(char value) => Write(value.ToString());

		public override void WriteLine(string? value) =>
			Write((value ?? string.Empty) + NewLine);

		public override Task WriteAsync(string? value)
		{
			Write(value);
			return Task.CompletedTask;
		}

		public override Task WriteAsync(char value)
		{
			Write(value);
			return Task.CompletedTask;
		}

		public override Task WriteLineAsync(string? value)
		{
			WriteLine(value);
			return Task.CompletedTask;
		}

		public override Task FlushAsync(CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}
}
