using System.Text;
using System.Runtime.InteropServices;

namespace Repl;

/// <summary>
/// Custom key-by-key console reader that supports Esc detection, cancellation,
/// cursor movement (Left/Right/Home/End), in-place editing, history navigation,
/// and interactive autocomplete.
/// Works both locally (Console) and remotely (IReplKeyReader + TextWriter).
/// </summary>
internal static partial class ConsoleLineReader
{
	private static readonly AsyncLocal<int?> AvailableOverlayRowsOverride = new();

	internal readonly record struct ReadResult(string? Line, bool Escaped);

	internal readonly record struct AutocompleteRequest(string Input, int Cursor, bool MenuRequested);

	internal readonly record struct AutocompleteSuggestion(
		string Value,
		string? DisplayText = null,
		string? Description = null,
		AutocompleteSuggestionKind Kind = AutocompleteSuggestionKind.Command,
		bool IsSelectable = true)
	{
		public string DisplayText { get; } = string.IsNullOrWhiteSpace(DisplayText) ? Value : DisplayText;
	}

	internal enum AutocompleteSuggestionKind
	{
		Command = 0,
		Context = 1,
		Parameter = 2,
		Ambiguous = 3,
		Invalid = 4,
	}

	[StructLayout(LayoutKind.Auto)]
	internal readonly record struct TokenClassification(
		int Start,
		int Length,
		AutocompleteSuggestionKind Kind);

	internal readonly record struct AutocompleteColorStyles(
		string CommandStyle,
		string ContextStyle,
		string ParameterStyle,
		string AmbiguousStyle,
		string ErrorStyle,
		string HintLabelStyle)
	{
		public static AutocompleteColorStyles Empty { get; } = new(
			CommandStyle: string.Empty,
			ContextStyle: string.Empty,
			ParameterStyle: string.Empty,
			AmbiguousStyle: string.Empty,
			ErrorStyle: string.Empty,
			HintLabelStyle: string.Empty);
	}

	internal readonly record struct AutocompleteResult(
		int ReplaceStart,
		int ReplaceLength,
		IReadOnlyList<AutocompleteSuggestion> Suggestions,
		string? HintLine = null,
		IReadOnlyList<TokenClassification>? TokenClassifications = null);

	internal enum AutocompleteRenderMode
	{
		Off = 0,
		Basic = 1,
		Rich = 2,
	}

	internal delegate ValueTask<AutocompleteResult?> AutocompleteResolver(
		AutocompleteRequest request,
		CancellationToken cancellationToken);

	private sealed class LineEditorState(
		AutocompleteResolver? resolver,
		AutocompleteRenderMode renderMode,
		int maxVisibleSuggestions,
		AutocompletePresentation presentation,
		bool liveHintEnabled,
		bool colorizeInputLine,
		bool colorizeHintAndMenu,
		AutocompleteColorStyles colorStyles)
	{
		public AutocompleteResolver? Resolver { get; } = resolver;

		public AutocompleteRenderMode RenderMode { get; } = renderMode;

		public int MaxVisibleSuggestions { get; } = Math.Max(1, maxVisibleSuggestions);

		public AutocompletePresentation Presentation { get; } = presentation;

		public bool LiveHintEnabled { get; } = liveHintEnabled;

		public bool ColorizeInputLine { get; } = colorizeInputLine;

		public bool ColorizeHintAndMenu { get; } = colorizeHintAndMenu;

		public AutocompleteColorStyles ColorStyles { get; } = colorStyles;

		public int ConsecutiveTabPresses { get; set; }

		public bool IsMenuOpen { get; set; }

		public int SelectedIndex { get; set; }

		public AutocompleteResult? LastResult { get; set; }

		public string? CurrentHintLine { get; set; }

		public int RenderedOverlayLines { get; set; }

		public IReadOnlyList<TokenClassification> TokenClassifications { get; set; } = [];

		public int RenderedInputLength { get; set; }
	}

	/// <summary>
	/// Reads a line of input from the console.
	/// When the console is redirected (tests, pipes), falls back to <c>Console.In.ReadLineAsync</c>.
	/// When interactive, reads key-by-key with Esc detection and cancellation support.
	/// </summary>
	internal static async ValueTask<ReadResult> ReadLineAsync(CancellationToken ct)
	{
		if (ReplSessionIO.KeyReader is { } keyReader)
		{
			return await ReadLineRemoteAsync(keyReader, navigator: null, editor: null, ct).ConfigureAwait(false);
		}

		if (Console.IsInputRedirected || ReplSessionIO.IsSessionActive)
		{
			var line = await ReplSessionIO.Input.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();
			return new ReadResult(line, Escaped: false);
		}

		return await Task.Run(() => ReadLineSync(navigator: null, editor: null, ct), ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Reads a line of input from the console with optional history navigation (Up/Down arrows).
	/// </summary>
	internal static async ValueTask<ReadResult> ReadLineAsync(IHistoryProvider? history, CancellationToken ct) =>
		await ReadLineAsync(
			history,
			autocompleteResolver: null,
			renderMode: AutocompleteRenderMode.Off,
			maxVisibleSuggestions: 8,
			presentation: AutocompletePresentation.Hybrid,
			liveHintEnabled: false,
			colorizeInputLine: false,
			colorizeHintAndMenu: false,
			AutocompleteColorStyles.Empty,
			ct).ConfigureAwait(false);

	internal static async ValueTask<ReadResult> ReadLineAsync(
		IHistoryProvider? history,
		AutocompleteResolver? autocompleteResolver,
		AutocompleteRenderMode renderMode,
		int maxVisibleSuggestions,
		AutocompletePresentation presentation,
		bool liveHintEnabled,
		bool colorizeInputLine,
		bool colorizeHintAndMenu,
		AutocompleteColorStyles colorStyles,
		CancellationToken ct)
	{
		var editor = new LineEditorState(
			autocompleteResolver,
			renderMode,
			maxVisibleSuggestions,
			presentation,
			liveHintEnabled,
			colorizeInputLine,
			colorizeHintAndMenu,
			colorStyles);
		if (ReplSessionIO.KeyReader is { } keyReader)
		{
			var navigator = await CreateNavigatorAsync(history, ct).ConfigureAwait(false);
			return await ReadLineRemoteAsync(keyReader, navigator, editor, ct).ConfigureAwait(false);
		}

		if (Console.IsInputRedirected || ReplSessionIO.IsSessionActive)
		{
			var line = await ReplSessionIO.Input.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();
			return new ReadResult(line, Escaped: false);
		}

		var nav = await CreateNavigatorAsync(history, ct).ConfigureAwait(false);
		return await Task.Run(() => ReadLineSync(nav, editor, ct), ct).ConfigureAwait(false);
	}

	// ---------- Remote async path (IReplKeyReader + TextWriter) ----------

	private static async ValueTask<ReadResult> ReadLineRemoteAsync(
		IReplKeyReader keyReader,
		HistoryNavigator? navigator,
		LineEditorState? editor,
		CancellationToken ct)
	{
		var output = ReplSessionIO.Output;
		var buffer = new StringBuilder();
		var cursor = 0;
		var echo = new StringBuilder();

		while (true)
		{
			ct.ThrowIfCancellationRequested();

			ConsoleKeyInfo key;
			try
			{
				key = await keyReader.ReadKeyAsync(ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return new ReadResult(Line: null, Escaped: false);
			}

			var autocompleteHandling = await TryHandleAutocompleteKeyAsync(
				key,
				editor,
				buffer,
				cursor,
				echo,
				ct).ConfigureAwait(false);
			cursor = autocompleteHandling.Cursor;
			if (!autocompleteHandling.Handled)
			{
				var result = HandleKey(key, buffer, ref cursor, navigator, echo, editor);
				if (result is null)
				{
					await RefreshAssistAfterEditingAsync(editor, buffer, cursor, echo, ct).ConfigureAwait(false);
				}

				if (echo.Length > 0)
				{
					await output.WriteAsync(echo.ToString()).ConfigureAwait(false);
					await output.FlushAsync(ct).ConfigureAwait(false);
					echo.Clear();
				}

				if (result is not null)
				{
					return result.Value;
				}

				continue;
			}

			if (echo.Length > 0)
			{
				await output.WriteAsync(echo.ToString()).ConfigureAwait(false);
				await output.FlushAsync(ct).ConfigureAwait(false);
				echo.Clear();
			}
		}
	}

	// ---------- Console sync path ----------

	private static ReadResult ReadLineSync(HistoryNavigator? navigator, LineEditorState? editor, CancellationToken ct)
	{
		ConsoleInputGate.Gate.Wait(ct);
		try
		{
			return ReadLineCore(navigator, editor, ct);
		}
		finally
		{
			ConsoleInputGate.Gate.Release();
		}
	}

	private static ReadResult ReadLineCore(HistoryNavigator? navigator, LineEditorState? editor, CancellationToken ct)
	{
		var buffer = new StringBuilder();
		var cursor = 0;
		var echo = new StringBuilder();
		while (true)
		{
			ct.ThrowIfCancellationRequested();

			if (!Console.KeyAvailable)
			{
				Thread.Sleep(15);
				continue;
			}

			var key = Console.ReadKey(intercept: true);
#pragma warning disable VSTHRD002
			var autocompleteHandling = TryHandleAutocompleteKeyAsync(key, editor, buffer, cursor, echo, ct)
				.AsTask()
				.GetAwaiter()
				.GetResult();
#pragma warning restore VSTHRD002
			cursor = autocompleteHandling.Cursor;
			if (!autocompleteHandling.Handled)
			{
				var result = HandleKey(key, buffer, ref cursor, navigator, echo, editor);
				if (result is null)
				{
#pragma warning disable VSTHRD002
					RefreshAssistAfterEditingAsync(editor, buffer, cursor, echo, ct)
						.AsTask()
						.GetAwaiter()
						.GetResult();
#pragma warning restore VSTHRD002
				}

				if (echo.Length > 0)
				{
					Console.Write(echo.ToString());
					echo.Clear();
				}

				if (result is not null)
				{
					return result.Value;
				}

				continue;
			}

			if (echo.Length > 0)
			{
				Console.Write(echo.ToString());
				echo.Clear();
			}
		}
	}

	// ---------- Key handling (shared by both paths) ----------

	internal static ReadResult? HandleKey(
		ConsoleKeyInfo key,
		StringBuilder buffer,
		ref int cursor,
		HistoryNavigator? navigator,
		StringBuilder echo) =>
		HandleKey(key, buffer, ref cursor, navigator, echo, editor: null);

	private static ReadResult? HandleKey(
		ConsoleKeyInfo key,
		StringBuilder buffer,
		ref int cursor,
		HistoryNavigator? navigator,
		StringBuilder echo,
		LineEditorState? editor)
	{
		if (editor is { IsMenuOpen: true, RenderMode: not AutocompleteRenderMode.Rich }
			&& key.Key is not ConsoleKey.UpArrow
			and not ConsoleKey.DownArrow
			and not ConsoleKey.Tab
			and not ConsoleKey.Enter
			and not ConsoleKey.Escape)
		{
			editor.IsMenuOpen = false;
			ClearRenderedMenu(editor, echo);
		}

		if (key.Key != ConsoleKey.Tab)
		{
			ResetTabState(editor);
		}

		if (TryHandleEscapeOrEnter(key, editor, buffer, ref cursor, echo, out var result))
		{
			return result;
		}

		HandleEditingKey(key, buffer, ref cursor, navigator, echo);
		return null;
	}

	private static bool TryHandleEscapeOrEnter(
		ConsoleKeyInfo key,
		LineEditorState? editor,
		StringBuilder buffer,
		ref int cursor,
		StringBuilder echo,
		out ReadResult? result)
	{
		if (key.Key == ConsoleKey.Escape)
		{
			if (editor is { IsMenuOpen: true })
			{
				editor.IsMenuOpen = false;
				ClearRenderedMenu(editor, echo);
				result = null;
				return true;
			}

			ReplaceLine(buffer, ref cursor, string.Empty, echo);
			result = new ReadResult(Line: null, Escaped: true);
			return true;
		}

		if (key.Key == ConsoleKey.Enter)
		{
			if (editor is not null)
			{
				ClearRenderedMenu(editor, echo);
			}

			MoveCursorToEnd(buffer, ref cursor, echo);
			echo.Append("\r\n");
			result = new ReadResult(buffer.ToString(), Escaped: false);
			return true;
		}

		result = null;
		return false;
	}

	private static void HandleEditingKey(
		ConsoleKeyInfo key,
		StringBuilder buffer,
		ref int cursor,
		HistoryNavigator? navigator,
		StringBuilder echo)
	{
		switch (key.Key)
		{
			case ConsoleKey.Backspace:
				HandleBackspace(buffer, ref cursor, echo);
				break;
			case ConsoleKey.Delete:
				HandleDelete(buffer, ref cursor, echo);
				break;
			case ConsoleKey.LeftArrow:
				if (cursor > 0)
				{
					cursor--;
					echo.Append('\b');
				}

				break;
			case ConsoleKey.RightArrow:
				if (cursor < buffer.Length)
				{
					echo.Append(buffer[cursor]);
					cursor++;
				}

				break;
			case ConsoleKey.Home:
				MoveCursorToStart(ref cursor, echo);
				break;
			case ConsoleKey.End:
				MoveCursorToEnd(buffer, ref cursor, echo);
				break;
			case ConsoleKey.UpArrow when navigator is not null:
				navigator.UpdateCurrent(buffer.ToString());
				if (navigator.TryMoveUp(out var upEntry))
				{
					ReplaceLine(buffer, ref cursor, upEntry, echo);
				}

				break;
			case ConsoleKey.DownArrow when navigator is not null:
				navigator.UpdateCurrent(buffer.ToString());
				if (navigator.TryMoveDown(out var downEntry))
				{
					ReplaceLine(buffer, ref cursor, downEntry, echo);
				}

				break;
			default:
				if (key.KeyChar != '\0')
				{
					InsertChar(buffer, ref cursor, key.KeyChar, echo);
				}

				break;
		}
	}

	private static void ResetTabState(LineEditorState? editor)
	{
		if (editor is null)
		{
			return;
		}

		editor.ConsecutiveTabPresses = 0;
		editor.LastResult = null;
	}

	private static void InsertChar(StringBuilder buffer, ref int cursor, char ch, StringBuilder echo)
	{
		buffer.Insert(cursor, ch);
		WriteFromCursor(buffer, cursor, echo);
		cursor++;
		MoveBack(buffer.Length - cursor, echo);
	}

	private static void HandleBackspace(StringBuilder buffer, ref int cursor, StringBuilder echo)
	{
		if (cursor == 0)
		{
			return;
		}

		buffer.Remove(cursor - 1, 1);
		cursor--;
		echo.Append('\b');
		WriteFromCursor(buffer, cursor, echo);
		echo.Append(' ');
		MoveBack(buffer.Length - cursor + 1, echo);
	}

	private static void HandleDelete(StringBuilder buffer, ref int cursor, StringBuilder echo)
	{
		if (cursor >= buffer.Length)
		{
			return;
		}

		buffer.Remove(cursor, 1);
		WriteFromCursor(buffer, cursor, echo);
		echo.Append(' ');
		MoveBack(buffer.Length - cursor + 1, echo);
	}

	private static void ReplaceLine(
		StringBuilder buffer,
		ref int cursor,
		string newText,
		StringBuilder echo)
	{
		MoveCursorToStart(ref cursor, echo);
		echo.Append(newText);
		var overflow = buffer.Length - newText.Length;
		if (overflow > 0)
		{
			echo.Append(' ', overflow);
			MoveBack(overflow, echo);
		}

		buffer.Clear();
		buffer.Append(newText);
		cursor = newText.Length;
	}

	private static void MoveCursorToStart(ref int cursor, StringBuilder echo)
	{
		if (cursor > 0)
		{
			MoveBack(cursor, echo);
			cursor = 0;
		}
	}

	private static void MoveCursorToEnd(StringBuilder buffer, ref int cursor, StringBuilder echo)
	{
		if (cursor < buffer.Length)
		{
			WriteFromCursor(buffer, cursor, echo);
			cursor = buffer.Length;
		}
	}

	private static void WriteFromCursor(StringBuilder buffer, int cursor, StringBuilder echo)
	{
		for (var i = cursor; i < buffer.Length; i++)
		{
			echo.Append(buffer[i]);
		}
	}

	private static void MoveBack(int count, StringBuilder echo)
	{
		if (count > 0)
		{
			echo.Append('\b', count);
		}
	}

	private static async ValueTask<HistoryNavigator?> CreateNavigatorAsync(
		IHistoryProvider? history,
		CancellationToken ct)
	{
		if (history is null)
		{
			return null;
		}

		var entries = await history.GetRecentAsync(500, ct).ConfigureAwait(false);
		return entries.Count > 0 ? new HistoryNavigator(entries) : null;
	}
}
