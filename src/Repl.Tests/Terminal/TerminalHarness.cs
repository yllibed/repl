using System.Text;
using XTerm;
using XTerm.Options;

namespace Repl.Tests.TerminalSupport;

internal sealed class TerminalHarness
{
	internal sealed record TerminalFrame(
		int CursorX,
		int CursorY,
		IReadOnlyList<string> Lines);

	private readonly XTerm.Terminal _terminal;
	private readonly int _cols;
	private readonly int _rows;
	private readonly StringBuilder _raw = new();
	private readonly List<TerminalFrame> _frames = [];

	public TerminalHarness(int cols = 80, int rows = 24)
	{
		_cols = cols;
		_rows = rows;
		_terminal = new XTerm.Terminal(new TerminalOptions
		{
			Cols = cols,
			Rows = rows,
		});
		Writer = new TerminalWriter(this);
	}

	public TextWriter Writer { get; }

	public int CursorX => _terminal.Buffer.X;

	public int CursorY => _terminal.Buffer.Y;

	public string RawOutput => _raw.ToString();

	public int Cols => _cols;

	public int Rows => _rows;

	public IReadOnlyList<TerminalFrame> Frames => _frames;

	public string GetLine(int row) => _terminal.GetLine(row);

	public IReadOnlyList<string> GetVisibleLines()
	{
		var lines = new string[_rows];
		for (var row = 0; row < _rows; row++)
		{
			lines[row] = _terminal.GetLine(row);
		}

		return lines;
	}

	private void Write(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}

		_raw.Append(text);
		_terminal.Write(text);
		_frames.Add(new TerminalFrame(_terminal.Buffer.X, _terminal.Buffer.Y, [.. GetVisibleLines()]));
	}

	private sealed class TerminalWriter(TerminalHarness owner) : TextWriter
	{
		private readonly TerminalHarness _owner = owner;

		public override Encoding Encoding => Encoding.UTF8;

		public override void Write(string? value)
		{
			if (!string.IsNullOrEmpty(value))
			{
				_owner.Write(value);
			}
		}

		public override Task WriteAsync(string? value)
		{
			Write(value);
			return Task.CompletedTask;
		}

		public override Task FlushAsync(CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}
}
