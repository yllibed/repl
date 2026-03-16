namespace Repl.TerminalGui;

/// <summary>
/// A composite view containing a prompt label and a command input field.
/// Fires <see cref="CommandSubmitted"/> when the user presses Enter.
/// Supports Up/Down command history navigation.
/// </summary>
public sealed class ReplInputView : View
{
	private readonly Label _promptLabel;
	private readonly TextField _textField;
	private readonly List<string> _history = [];
	private int _historyIndex = -1;
	private IHistoryProvider? _historyProvider;
	private bool _historyLoaded;

	/// <summary>
	/// Initializes a new instance of the <see cref="ReplInputView"/> class.
	/// </summary>
	public ReplInputView()
	{
		Height = 1;

		_promptLabel = new Label
		{
			Text = "> ",
			X = 0,
			Y = 0,
			Width = Dim.Auto(DimAutoStyle.Text),
		};

		_textField = new TextField
		{
			X = Pos.Right(_promptLabel),
			Y = 0,
			Width = Dim.Fill(),
		};

		_textField.Accepting += OnAccepting;
		_textField.KeyDown += OnKeyDown;

		Add(_promptLabel, _textField);
		CanFocus = true;
	}

	/// <summary>
	/// Raised when the user submits a command (presses Enter).
	/// </summary>
	public event EventHandler<CommandSubmittedEventArgs>? CommandSubmitted;

	/// <summary>
	/// Gets or sets the prompt text (e.g. "> " or "[client]> ").
	/// </summary>
	public string Prompt
	{
		get => _promptLabel.Text ?? "> ";
		set => App?.Invoke(() =>
		{
			_promptLabel.Text = value;
			SetNeedsDraw();
		});
	}

	/// <summary>
	/// Sets keyboard focus to the input text field.
	/// </summary>
	public void FocusInput() => _textField.SetFocus();

	/// <summary>
	/// Sets the history provider for cross-session command history.
	/// When set, submitted commands are persisted and prior history is loaded on first navigation.
	/// </summary>
	public void SetHistoryProvider(IHistoryProvider provider)
	{
		_historyProvider = provider;
	}

	private void OnAccepting(object? sender, CommandEventArgs e)
	{
		var text = _textField.Text ?? string.Empty;

		if (!string.IsNullOrWhiteSpace(text))
		{
			_history.Add(text);
			PersistToHistoryProvider(text);
		}

		_historyIndex = -1;
		_textField.Text = string.Empty;

		CommandSubmitted?.Invoke(this, new CommandSubmittedEventArgs(text));
		e.Handled = true;
	}

	private void OnKeyDown(object? sender, Key e)
	{
		if (e == Key.CursorUp)
		{
			NavigateHistory(direction: -1);
			e.Handled = true;
		}
		else if (e == Key.CursorDown)
		{
			NavigateHistory(direction: 1);
			e.Handled = true;
		}
	}

	private void NavigateHistory(int direction)
	{
		if (!_historyLoaded && _historyProvider is not null)
		{
			_historyLoaded = true;
			LoadHistoryFromProvider();
		}

		if (_history.Count == 0)
		{
			return;
		}

		if (_historyIndex < 0)
		{
			_historyIndex = direction < 0 ? _history.Count - 1 : 0;
		}
		else
		{
			_historyIndex += direction;
		}

		_historyIndex = Math.Clamp(_historyIndex, 0, _history.Count - 1);
		_textField.Text = _history[_historyIndex];
		_textField.MoveEnd();
	}

#pragma warning disable VSTHRD002 // Synchronous wait — IHistoryProvider implementations are typically synchronous (InMemoryHistoryProvider)

	private void LoadHistoryFromProvider()
	{
		if (_historyProvider is null)
		{
			return;
		}

		var entries = _historyProvider.GetRecentAsync(500).AsTask().GetAwaiter().GetResult();

		for (var i = 0; i < entries.Count; i++)
		{
			if (!_history.Contains(entries[i], StringComparer.Ordinal))
			{
				_history.Insert(i, entries[i]);
			}
		}
	}

	private void PersistToHistoryProvider(string text)
	{
		_historyProvider?.AddAsync(text).AsTask().GetAwaiter().GetResult();
	}

#pragma warning restore VSTHRD002
}
