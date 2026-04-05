using System.Text;

namespace Repl;

internal static partial class ConsoleLineReader
{
	private static async ValueTask RefreshAssistAfterEditingAsync(
		LineEditorState? editor,
		StringBuilder buffer,
		int cursor,
		StringBuilder echo,
		CancellationToken ct)
	{
		if (editor?.Resolver is null || editor.RenderMode != AutocompleteRenderMode.Rich)
		{
			return;
		}

		if (!editor.LiveHintEnabled && !editor.IsMenuOpen)
		{
			return;
		}

		var assist = await editor.Resolver(
				new AutocompleteRequest(buffer.ToString(), cursor, MenuRequested: editor.IsMenuOpen),
				ct)
			.ConfigureAwait(false);
		ApplyAssistResult(editor, assist);
		if (editor.IsMenuOpen && editor.LastResult is null)
		{
			editor.IsMenuOpen = false;
		}

		RenderOverlay(editor, buffer, cursor, echo);
	}

	private static async ValueTask<(bool Handled, int Cursor)> TryHandleAutocompleteKeyAsync(
		ConsoleKeyInfo key,
		LineEditorState? editor,
		StringBuilder buffer,
		int cursor,
		StringBuilder echo,
		CancellationToken ct)
	{
		if (editor is null
			|| editor.RenderMode == AutocompleteRenderMode.Off
			|| editor.Resolver is null)
		{
			return (false, cursor);
		}

		if (editor.IsMenuOpen && TryHandleOpenMenuKey(key, editor, buffer, ref cursor, echo))
		{
			return (true, cursor);
		}

		if (key.Key != ConsoleKey.Tab)
		{
			return (false, cursor);
		}

		return await HandleTabAutocompleteAsync(editor, buffer, cursor, echo, ct).ConfigureAwait(false);
	}

	private static bool TryHandleOpenMenuKey(
		ConsoleKeyInfo key,
		LineEditorState editor,
		StringBuilder buffer,
		ref int cursor,
		StringBuilder echo)
	{
		if (key.Key == ConsoleKey.UpArrow)
		{
			MoveSelection(editor, direction: -1);
			RenderMenu(editor, buffer, cursor, echo);
			return true;
		}

		if (key.Key == ConsoleKey.DownArrow || (key.Key == ConsoleKey.Tab && key.Modifiers.HasFlag(ConsoleModifiers.Shift)))
		{
			MoveSelection(editor, direction: 1);
			RenderMenu(editor, buffer, cursor, echo);
			return true;
		}

		if (key.Key == ConsoleKey.Enter)
		{
			ApplySelectedSuggestion(editor, buffer, ref cursor, echo);
			editor.IsMenuOpen = false;
			editor.ConsecutiveTabPresses = 0;
			ClearRenderedMenu(editor, echo);
			return true;
		}

		return false;
	}

	private static async ValueTask<(bool Handled, int Cursor)> HandleTabAutocompleteAsync(
		LineEditorState editor,
		StringBuilder buffer,
		int cursor,
		StringBuilder echo,
		CancellationToken ct)
	{
		var request = new AutocompleteRequest(buffer.ToString(), cursor, MenuRequested: ShouldOpenMenu(editor));
		var assist = await editor.Resolver!(request, ct).ConfigureAwait(false);
		ApplyAssistResult(editor, assist);
		if (editor.LastResult is not { } result || result.Suggestions.Count == 0)
		{
			editor.ConsecutiveTabPresses = 0;
			editor.IsMenuOpen = false;
			if (editor.RenderMode == AutocompleteRenderMode.Rich)
			{
				RenderOverlay(editor, buffer, cursor, echo);
			}

			return (true, cursor);
		}

		if (!ShouldOpenMenu(editor) && TryApplyFirstTabCompletion(result, buffer, ref cursor, echo))
		{
			editor.ConsecutiveTabPresses = 0;
			editor.IsMenuOpen = false;
			await RefreshAssistAfterEditingAsync(editor, buffer, cursor, echo, ct).ConfigureAwait(false);
			return (true, cursor);
		}

		editor.IsMenuOpen = true;
		editor.ConsecutiveTabPresses++;
		if (editor.SelectedIndex >= result.Suggestions.Count)
		{
			editor.SelectedIndex = 0;
		}

		RenderMenu(editor, buffer, cursor, echo);
		return (true, cursor);
	}

	private static void ApplyAssistResult(LineEditorState editor, AutocompleteResult? assist)
	{
		var previousSelectedValue = editor.LastResult is { } prior
			&& prior.Suggestions.Count > 0
			&& editor.SelectedIndex >= 0
			&& editor.SelectedIndex < prior.Suggestions.Count
				? prior.Suggestions[editor.SelectedIndex].Value
				: null;

		editor.CurrentHintLine = assist?.HintLine;
		editor.TokenClassifications = assist?.TokenClassifications ?? [];
		if (assist is not { Suggestions.Count: > 0 } resolved)
		{
			editor.LastResult = null;
			editor.SelectedIndex = 0;
			return;
		}

		editor.LastResult = resolved;
		if (string.IsNullOrEmpty(previousSelectedValue))
		{
			if (editor.SelectedIndex >= resolved.Suggestions.Count)
			{
				editor.SelectedIndex = 0;
			}

			return;
		}

		var selected = resolved.Suggestions
			.Select((suggestion, index) => (suggestion, index))
			.FirstOrDefault(item => string.Equals(item.suggestion.Value, previousSelectedValue, StringComparison.OrdinalIgnoreCase));
		editor.SelectedIndex = selected == default ? 0 : selected.index;
	}

	private static bool ShouldOpenMenu(LineEditorState editor) =>
		editor.Presentation switch
		{
			AutocompletePresentation.MenuFirst => true,
			AutocompletePresentation.Classic => editor.ConsecutiveTabPresses >= 1,
			_ => editor.ConsecutiveTabPresses >= 1,
		};

	private static void MoveSelection(LineEditorState editor, int direction)
	{
		if (editor.LastResult is not { } result || result.Suggestions.Count == 0)
		{
			editor.SelectedIndex = 0;
			return;
		}

		var selectable = result.Suggestions
			.Select((suggestion, index) => (suggestion, index))
			.Where(item => item.suggestion.IsSelectable)
			.Select(item => item.index)
			.ToArray();
		if (selectable.Length == 0)
		{
			editor.SelectedIndex = 0;
			return;
		}

		var current = Array.IndexOf(selectable, editor.SelectedIndex);
		current = current < 0 ? 0 : current;
		var next = (current + direction) % selectable.Length;
		if (next < 0)
		{
			next += selectable.Length;
		}

		editor.SelectedIndex = selectable[next];
	}

	private static bool TryApplyFirstTabCompletion(
		AutocompleteResult result,
		StringBuilder buffer,
		ref int cursor,
		StringBuilder echo)
	{
		var selectableSuggestions = result.Suggestions.Where(static suggestion => suggestion.IsSelectable).ToArray();
		if (selectableSuggestions.Length == 1)
		{
			ApplySuggestion(result, selectableSuggestions[0], buffer, ref cursor, echo);
			return true;
		}

		if (selectableSuggestions.Length == 0)
		{
			return false;
		}

		var common = LongestCommonPrefix(selectableSuggestions.Select(static suggestion => suggestion.Value));
		if (string.IsNullOrEmpty(common))
		{
			return false;
		}

		var current = buffer
			.ToString(result.ReplaceStart, Math.Min(result.ReplaceLength, buffer.Length - result.ReplaceStart));
		if (common.Length <= current.Length)
		{
			return false;
		}

		ApplySuggestion(result, new AutocompleteSuggestion(common), buffer, ref cursor, echo);
		return true;
	}

	private static void ApplySelectedSuggestion(
		LineEditorState editor,
		StringBuilder buffer,
		ref int cursor,
		StringBuilder echo)
	{
		if (editor.LastResult is not { } result || result.Suggestions.Count == 0)
		{
			return;
		}

		var selected = result.Suggestions[Math.Clamp(editor.SelectedIndex, 0, result.Suggestions.Count - 1)];
		if (!selected.IsSelectable)
		{
			return;
		}

		ApplySuggestion(result, selected, buffer, ref cursor, echo);
	}

	private static void ApplySuggestion(
		AutocompleteResult result,
		AutocompleteSuggestion suggestion,
		StringBuilder buffer,
		ref int cursor,
		StringBuilder echo)
	{
		var replaceStart = Math.Clamp(result.ReplaceStart, 0, buffer.Length);
		var replaceLength = Math.Clamp(result.ReplaceLength, 0, buffer.Length - replaceStart);
		buffer.Remove(replaceStart, replaceLength);
		buffer.Insert(replaceStart, suggestion.Value);
		var targetCursor = replaceStart + suggestion.Value.Length;
		ReplaceBuffer(buffer, ref cursor, targetCursor, echo);
	}

	private static void RenderMenu(LineEditorState editor, StringBuilder buffer, int cursor, StringBuilder echo)
	{
		if (editor.LastResult is not { } result || result.Suggestions.Count == 0)
		{
			return;
		}

		if (editor.RenderMode == AutocompleteRenderMode.Rich)
		{
			RenderOverlay(editor, buffer, cursor, echo);
			return;
		}

		var renderWidth = ResolveRenderWidth();
		var count = Math.Min(editor.MaxVisibleSuggestions, result.Suggestions.Count);
		var menuContentWidth = Math.Max(8, renderWidth - 2);
		var displayColumnWidth = ResolveMenuDisplayColumnWidth(result.Suggestions, count, menuContentWidth);
		echo.Append("\r\n");
		for (var i = 0; i < count; i++)
		{
			var suggestion = result.Suggestions[i];
			echo.Append(i == editor.SelectedIndex ? "> " : "  ");
			var formatted = FormatSuggestionForMenu(suggestion, menuContentWidth, displayColumnWidth);
			echo.Append(ApplySuggestionStyle(editor, suggestion, formatted));
			echo.Append("\r\n");
		}

		if (result.Suggestions.Count > count)
		{
			echo.Append("... ").Append(result.Suggestions.Count - count).Append(" more\r\n");
		}

		WriteFromCursor(buffer, cursor: 0, echo);
		MoveBack(buffer.Length - cursor, echo);
	}

	private static void RenderOverlay(LineEditorState editor, StringBuilder buffer, int cursor, StringBuilder echo)
	{
		if (editor.RenderMode != AutocompleteRenderMode.Rich)
		{
			return;
		}

		ClearRenderedMenu(editor, echo);
		var hasMenu = editor.IsMenuOpen && editor.LastResult is { Suggestions.Count: > 0 };
		var hint = editor.LiveHintEnabled ? editor.CurrentHintLine : null;
		var hasInputColors = editor.ColorizeInputLine && editor.TokenClassifications.Count > 0;
		if (!hasMenu && string.IsNullOrWhiteSpace(hint) && !hasInputColors)
		{
			return;
		}

		var availableRows = ResolveAvailableOverlayRows();
		var forcedOverlayScroll = false;
		if (availableRows <= 0)
		{
			if (ShouldForceOverlayScrollInLocalConsole())
			{
				availableRows = 1;
				forcedOverlayScroll = true;
			}
			else
			{
				availableRows = int.MaxValue;
			}
		}

		var overlayLines = BuildOverlayLines(editor, hint, availableRows);
		if (overlayLines.Count == 0)
		{
			RenderInputOnly(editor, buffer, cursor, echo);
			return;
		}

		if (forcedOverlayScroll)
		{
			FlushAndWriteOverlayRelative(overlayLines, echo);
		}
		else
		{
			WriteOverlayLines(overlayLines, echo);
			AppendRestoreCursor(echo);
		}

		editor.RenderedOverlayLines = overlayLines.Count;
		if (editor.ColorizeInputLine && !forcedOverlayScroll)
		{
			RenderStyledInputLine(editor, buffer, cursor, echo);
		}
	}

	private static void RenderInputOnly(
		LineEditorState editor,
		StringBuilder buffer,
		int cursor,
		StringBuilder echo)
	{
		editor.RenderedOverlayLines = 0;
		if (editor.ColorizeInputLine)
		{
			RenderStyledInputLine(editor, buffer, cursor, echo);
		}
	}

	private static List<string> BuildOverlayLines(
		LineEditorState editor,
		string? hint,
		int availableRows)
	{
		var renderWidth = ResolveRenderWidth();
		var overlayLines = new List<string>(Math.Min(editor.MaxVisibleSuggestions + 2, availableRows));
		if (!string.IsNullOrWhiteSpace(hint))
		{
			overlayLines.Add(FormatHintLine(editor, hint, renderWidth));
		}

		if (editor.IsMenuOpen && editor.LastResult is { Suggestions.Count: > 0 } result && overlayLines.Count < availableRows)
		{
			AppendMenuLines(editor, result, renderWidth, availableRows, overlayLines);
		}

		return overlayLines;
	}

	private static void AppendMenuLines(
		LineEditorState editor,
		AutocompleteResult result,
		int renderWidth,
		int availableRows,
		List<string> overlayLines)
	{
		var menuSlots = availableRows - overlayLines.Count;
		var visibleCount = Math.Min(
			editor.MaxVisibleSuggestions,
			Math.Min(result.Suggestions.Count, menuSlots));
		var menuContentWidth = Math.Max(8, renderWidth - 2);
		var displayColumnWidth = ResolveMenuDisplayColumnWidth(result.Suggestions, visibleCount, menuContentWidth);
		for (var i = 0; i < visibleCount; i++)
		{
			var marker = i == editor.SelectedIndex ? ">" : " ";
			var suggestion = result.Suggestions[i];
			var formatted = FormatSuggestionForMenu(suggestion, menuContentWidth, displayColumnWidth);
			overlayLines.Add(string.Concat(
				"\u001b[38;5;110m",
				marker,
				"\u001b[0m ",
				ApplySuggestionStyle(editor, suggestion, formatted)));
		}

		if (result.Suggestions.Count > visibleCount && overlayLines.Count < availableRows)
		{
			overlayLines.Add($"... {result.Suggestions.Count - visibleCount} more");
		}
	}

	private static void WriteOverlayLines(List<string> overlayLines, StringBuilder echo)
	{
		AppendSaveCursor(echo);
		echo.Append("\r\n"); // next line, column 1 (portable across terminals)
		for (var i = 0; i < overlayLines.Count; i++)
		{
			if (i > 0)
			{
				echo.Append("\r\n");
			}

			echo.Append(overlayLines[i]);
		}
	}

	/// <summary>
	/// Flushes any pending echo so that <see cref="Console.CursorLeft"/> is accurate,
	/// then writes overlay lines using relative cursor movement.
	/// </summary>
	private static void FlushAndWriteOverlayRelative(List<string> overlayLines, StringBuilder echo)
	{
		// Flush pending key echo so Console.CursorLeft reflects the
		// actual screen cursor position before we capture it.
		if (echo.Length > 0)
		{
			Console.Write(echo.ToString());
			echo.Clear();
		}

		var cursorCol = GetConsoleCursorLeft();
		WriteOverlayLinesRelative(overlayLines, cursorCol, echo);
	}

	/// <summary>
	/// Writes overlay lines below the input and returns to the input line using
	/// relative cursor movement (CUU + CHA) instead of save/restore.  This avoids
	/// corruption on Windows consoles where the saved cursor position is not
	/// adjusted after a scroll.
	/// </summary>
	private static void WriteOverlayLinesRelative(
		List<string> overlayLines,
		int cursorCol,
		StringBuilder echo)
	{
		for (var i = 0; i < overlayLines.Count; i++)
		{
			echo.Append("\r\n");
			echo.Append(overlayLines[i]);
			echo.Append("\u001b[K"); // clear to end of line
		}

		// Return to the input line with CUU (cursor up) and CHA (cursor horizontal absolute).
		echo.Append("\u001b[").Append(overlayLines.Count).Append('A');
		echo.Append("\u001b[").Append(cursorCol + 1).Append('G');
	}

	private static int ResolveAvailableOverlayRows()
	{
		if (AvailableOverlayRowsOverride.Value is { } overriddenRows)
		{
			return Math.Max(0, overriddenRows);
		}

		if (ReplSessionIO.IsSessionActive || Console.IsOutputRedirected)
		{
			return int.MaxValue;
		}

		try
		{
			var relativeCursorRow = Console.CursorTop - Console.WindowTop;
			return Math.Max(0, Console.WindowHeight - relativeCursorRow - 1);
		}
		catch
		{
			return int.MaxValue;
		}
	}

	private static bool ShouldForceOverlayScrollInLocalConsole() =>
		!ReplSessionIO.IsSessionActive && !Console.IsOutputRedirected;

	private static int GetConsoleCursorLeft()
	{
		try
		{
			return Console.CursorLeft;
		}
		catch
		{
			return 0;
		}
	}

	internal static IDisposable OverrideAvailableOverlayRowsForTests(int? rows)
	{
		var previous = AvailableOverlayRowsOverride.Value;
		AvailableOverlayRowsOverride.Value = rows;
		return new ResetOverrideScope(previous);
	}

	private sealed class ResetOverrideScope(int? previous) : IDisposable
	{
		private bool _disposed;

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			AvailableOverlayRowsOverride.Value = previous;
			_disposed = true;
		}
	}

	private static void ClearRenderedMenu(LineEditorState editor, StringBuilder echo)
	{
		if (editor.RenderedOverlayLines <= 0)
		{
			return;
		}

		if (editor.RenderMode == AutocompleteRenderMode.Rich)
		{
			AppendSaveCursor(echo);
			echo.Append("\r\n"); // next line, column 1
			echo.Append("\u001b[J"); // clear to end of screen
			AppendRestoreCursor(echo);
		}

		editor.RenderedOverlayLines = 0;
	}

	private static int ResolveRenderWidth()
	{
		var width = ReplSessionIO.WindowSize?.Width;
		if (width is > 0)
		{
			return width.Value;
		}

		try
		{
			return Math.Max(20, Console.WindowWidth);
		}
		catch
		{
			return 120;
		}
	}

	private static void AppendSaveCursor(StringBuilder echo)
	{
		// Prefer CSI s/u for better compatibility with modern terminals
		// and keep DEC 7/8 as fallback.
		echo.Append("\u001b[s");
		echo.Append("\u001b7");
	}

	private static void AppendRestoreCursor(StringBuilder echo)
	{
		echo.Append("\u001b[u");
		echo.Append("\u001b8");
	}

	private static int ResolveMenuDisplayColumnWidth(
		IReadOnlyList<AutocompleteSuggestion> suggestions,
		int count,
		int maxContentWidth)
	{
		if (count <= 0)
		{
			return 0;
		}

		var width = 0;
		for (var i = 0; i < count; i++)
		{
			var display = NormalizeInline(suggestions[i].DisplayText);
			width = Math.Max(width, display.Length);
		}

		var maxColumnWidth = Math.Max(6, maxContentWidth - 10);
		return Math.Clamp(width, 0, maxColumnWidth);
	}

	private static string FormatSuggestionForMenu(
		AutocompleteSuggestion suggestion,
		int maxContentWidth,
		int displayColumnWidth)
	{
		var display = NormalizeInline(suggestion.DisplayText);
		var baseText = display;
		if (!string.IsNullOrWhiteSpace(suggestion.Description))
		{
			var description = NormalizeInline(suggestion.Description);
			var alignedWidth = Math.Clamp(displayColumnWidth, 0, Math.Max(1, maxContentWidth - 3));
			baseText = string.Concat(display.PadRight(alignedWidth), " - ", description);
		}

		return TruncateWithEllipsis(baseText, maxContentWidth);
	}

	private static string NormalizeInline(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

	private static string FormatHintLine(LineEditorState editor, string hint, int maxWidth)
	{
		var normalized = hint.Replace('\r', ' ').Replace('\n', ' ');
		var content = TruncateWithEllipsis(normalized, Math.Max(8, maxWidth));
		if (!editor.ColorizeHintAndMenu)
		{
			return content;
		}

		var style = hint.StartsWith("Param ", StringComparison.OrdinalIgnoreCase)
			? editor.ColorStyles.ParameterStyle
			: hint.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase)
				? editor.ColorStyles.ErrorStyle
				: editor.ColorStyles.HintLabelStyle;
		return ApplyStyle(style, content);
	}

	private static string ApplySuggestionStyle(LineEditorState editor, AutocompleteSuggestion suggestion, string text)
	{
		if (!editor.ColorizeHintAndMenu)
		{
			return text;
		}

		var style = suggestion.Kind switch
		{
			AutocompleteSuggestionKind.Command => editor.ColorStyles.CommandStyle,
			AutocompleteSuggestionKind.Context => editor.ColorStyles.ContextStyle,
			AutocompleteSuggestionKind.Parameter => editor.ColorStyles.ParameterStyle,
			AutocompleteSuggestionKind.Ambiguous => editor.ColorStyles.AmbiguousStyle,
			AutocompleteSuggestionKind.Invalid => editor.ColorStyles.ErrorStyle,
			_ => string.Empty,
		};
		return ApplyStyle(style, text);
	}

	private static string ApplyStyle(string style, string text)
	{
		if (string.IsNullOrEmpty(style))
		{
			return text;
		}

		return string.Concat(style, text, "\u001b[0m");
	}

	private static void RenderStyledInputLine(
		LineEditorState editor,
		StringBuilder buffer,
		int cursor,
		StringBuilder echo)
	{
		var plain = buffer.ToString();
		var styled = ColorizeInputLine(editor, plain);
		MoveBack(cursor, echo);
		echo.Append(styled);
		echo.Append("\u001b[K"); // clear any stale cells to end-of-line

		editor.RenderedInputLength = plain.Length;
		if (cursor < plain.Length)
		{
			MoveBack(plain.Length - cursor, echo);
		}
	}

	private static string ColorizeInputLine(LineEditorState editor, string input)
	{
		if (string.IsNullOrEmpty(input) || editor.TokenClassifications.Count == 0)
		{
			return input;
		}

		var classes = editor.TokenClassifications
			.Where(classification => classification.Length > 0)
			.OrderBy(classification => classification.Start)
			.ToArray();
		if (classes.Length == 0)
		{
			return input;
		}

		var output = new StringBuilder(input.Length + 32);
		var index = 0;
		foreach (var classification in classes)
		{
			var start = Math.Clamp(classification.Start, 0, input.Length);
			var end = Math.Clamp(classification.Start + classification.Length, 0, input.Length);
			if (end <= start)
			{
				continue;
			}

			if (start > index)
			{
				output.Append(input, index, start - index);
			}

			var token = input[start..end];
			var style = classification.Kind switch
			{
				AutocompleteSuggestionKind.Command => editor.ColorStyles.CommandStyle,
				AutocompleteSuggestionKind.Context => editor.ColorStyles.ContextStyle,
				AutocompleteSuggestionKind.Parameter => editor.ColorStyles.ParameterStyle,
				AutocompleteSuggestionKind.Ambiguous => editor.ColorStyles.AmbiguousStyle,
				AutocompleteSuggestionKind.Invalid => editor.ColorStyles.ErrorStyle,
				_ => string.Empty,
			};
			output.Append(ApplyStyle(style, token));
			index = end;
		}

		if (index < input.Length)
		{
			output.Append(input, index, input.Length - index);
		}

		return output.ToString();
	}

	private static string TruncateWithEllipsis(string text, int maxWidth)
	{
		if (string.IsNullOrEmpty(text) || maxWidth <= 0)
		{
			return string.Empty;
		}

		if (text.Length <= maxWidth)
		{
			return text;
		}

		if (maxWidth <= 3)
		{
			return text[..maxWidth];
		}

		return string.Concat(text.AsSpan(0, maxWidth - 3), "...");
	}

	private static string LongestCommonPrefix(IEnumerable<string> values)
	{
		using var enumerator = values.GetEnumerator();
		if (!enumerator.MoveNext())
		{
			return string.Empty;
		}

		var prefix = enumerator.Current ?? string.Empty;
		while (enumerator.MoveNext())
		{
			var current = enumerator.Current ?? string.Empty;
			var length = 0;
			while (length < prefix.Length
				&& length < current.Length
				&& char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(current[length]))
			{
				length++;
			}

			prefix = prefix[..length];
			if (prefix.Length == 0)
			{
				return string.Empty;
			}
		}

		return prefix;
	}

	private static void ReplaceBuffer(StringBuilder buffer, ref int cursor, int targetCursor, StringBuilder echo)
	{
		var oldLength = buffer.Length;
		var newText = buffer.ToString();
		MoveCursorToStart(ref cursor, echo);
		echo.Append(newText);
		var overflow = oldLength - newText.Length;
		if (overflow > 0)
		{
			echo.Append(' ', overflow);
			MoveBack(overflow, echo);
		}

		cursor = newText.Length;
		if (targetCursor < cursor)
		{
			MoveBack(cursor - targetCursor, echo);
			cursor = targetCursor;
		}
	}
}
