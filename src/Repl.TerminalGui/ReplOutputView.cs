using XTerm.Buffer;
using XTerm.Options;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Repl.TerminalGui;

/// <summary>
/// A Terminal.Gui view that renders REPL output using an XTerm.NET terminal emulator.
/// Interprets ANSI escape sequences (colors, styles, cursor movement) and renders
/// them natively in Terminal.Gui. Like xterm.js, but for a native TUI.
/// </summary>
public sealed class ReplOutputView : View
{
	private XTerm.Terminal? _terminal;
	private int _lastCols;
	private int _lastRows;

	/// <summary>
	/// Initializes a new instance of the <see cref="ReplOutputView"/> class.
	/// </summary>
	public ReplOutputView()
	{
		CanFocus = true;
	}

	/// <summary>
	/// Appends text to the terminal emulator. ANSI escape sequences are interpreted.
	/// Thread-safe: marshals to the UI thread automatically.
	/// </summary>
	internal void AppendText(string text)
	{
		App?.Invoke(() =>
		{
			EnsureTerminal();
			_terminal!.Write(text);
			_terminal.ScrollToBottom();
			SetNeedsDraw();
		});
	}

	/// <summary>
	/// Clears the terminal output.
	/// </summary>
	public void ClearOutput()
	{
		App?.Invoke(() =>
		{
			_terminal?.Write("\x1b[2J\x1b[H");
			SetNeedsDraw();
		});
	}

	/// <inheritdoc />
	protected override bool OnKeyDown(Key keyEvent)
	{
		if (_terminal is null)
		{
			return base.OnKeyDown(keyEvent);
		}

		if (keyEvent == Key.PageUp)
		{
			_terminal.ScrollLines(-Viewport.Height);
			SetNeedsDraw();
			return true;
		}

		if (keyEvent == Key.PageDown)
		{
			_terminal.ScrollLines(Viewport.Height);
			SetNeedsDraw();
			return true;
		}

		if (keyEvent == Key.Home.WithCtrl)
		{
			_terminal.ScrollToTop();
			SetNeedsDraw();
			return true;
		}

		if (keyEvent == Key.End.WithCtrl)
		{
			_terminal.ScrollToBottom();
			SetNeedsDraw();
			return true;
		}

		return base.OnKeyDown(keyEvent);
	}

	/// <inheritdoc />
	protected override bool OnDrawingContent(DrawContext? context)
	{
		EnsureTerminal();

		if (_terminal is null)
		{
			return false;
		}

		var viewport = Viewport;
		var buffer = _terminal.Buffer;

		for (var row = 0; row < viewport.Height && row < _terminal.Rows; row++)
		{
			var bufferRow = buffer.YDisp + row;

			if (bufferRow < 0 || bufferRow >= buffer.Lines.Length)
			{
				continue;
			}

			var line = buffer.Lines[bufferRow];
			if (line is null)
			{
				continue;
			}

			for (var col = 0; col < viewport.Width && col < _terminal.Cols; col++)
			{
				var cell = line[col];
				var content = cell.Content;

				if (string.IsNullOrEmpty(content) || string.Equals(content, "\0", StringComparison.Ordinal))
				{
					content = " ";
				}

				var attr = MapCellAttribute(cell);

				Move(col, row);
				App?.Driver?.SetAttribute(attr);
				App?.Driver?.AddStr(content);
			}
		}

		return true;
	}

	private void EnsureTerminal()
	{
		var viewport = Viewport;
		var cols = Math.Max(viewport.Width, 1);
		var rows = Math.Max(viewport.Height, 1);

		if (_terminal is null)
		{
			_terminal = new XTerm.Terminal(new TerminalOptions
			{
				Cols = cols,
				Rows = rows,
				Scrollback = 10000,
			});
			_terminal.Scrolled += (_, _) => App?.Invoke(() => SetNeedsDraw());
			_lastCols = cols;
			_lastRows = rows;

			return;
		}

		if (cols != _lastCols || rows != _lastRows)
		{
			_terminal.Resize(cols, rows);
			_lastCols = cols;
			_lastRows = rows;
		}
	}

	private static Attribute MapCellAttribute(BufferCell cell)
	{
		var fg = MapColor(cell.Attributes.GetFgColor(), cell.Attributes.GetFgColorMode(), isBackground: false);
		var bg = MapColor(cell.Attributes.GetBgColor(), cell.Attributes.GetBgColorMode(), isBackground: true);

		if (cell.Attributes.IsInverse())
		{
			(fg, bg) = (bg, fg);
		}

		return new Attribute(fg, bg);
	}

	private static Color MapColor(int color, int mode, bool isBackground)
	{
		return mode switch
		{
			0 => isBackground ? Color.Black : Color.White,
			1 => Map256Color(color),
			2 => new Color((color >> 16) & 0xFF, (color >> 8) & 0xFF, color & 0xFF),
			_ => isBackground ? Color.Black : Color.White,
		};
	}

	private static Color Map256Color(int index)
	{
		if (index is >= 0 and <= 7)
		{
			return StandardColor(index);
		}

		if (index is >= 8 and <= 15)
		{
			return BrightColor(index - 8);
		}

		if (index is >= 16 and <= 231)
		{
			var adjusted = index - 16;
			var r = adjusted / 36;
			var g = (adjusted % 36) / 6;
			var b = adjusted % 6;

			return new Color(r * 51, g * 51, b * 51);
		}

		if (index is >= 232 and <= 255)
		{
			var gray = (index - 232) * 10 + 8;

			return new Color(gray, gray, gray);
		}

		return Color.White;
	}

	private static Color StandardColor(int index) => index switch
	{
		0 => Color.Black,
		1 => new Color(170, 0, 0),
		2 => new Color(0, 170, 0),
		3 => new Color(170, 85, 0),
		4 => new Color(0, 0, 170),
		5 => new Color(170, 0, 170),
		6 => new Color(0, 170, 170),
		7 => new Color(170, 170, 170),
		_ => Color.White,
	};

	private static Color BrightColor(int index) => index switch
	{
		0 => new Color(85, 85, 85),
		1 => new Color(255, 85, 85),
		2 => new Color(85, 255, 85),
		3 => new Color(255, 255, 85),
		4 => new Color(85, 85, 255),
		5 => new Color(255, 85, 255),
		6 => new Color(85, 255, 255),
		7 => Color.White,
		_ => Color.White,
	};
}
