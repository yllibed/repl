namespace Repl;

/// <summary>
/// Interactive arrow-key menu rendering for AskChoice and AskMultiChoice.
/// </summary>
internal sealed partial class RichPromptInteractionHandler
{
	private const string AnsiReset = "\u001b[0m";
	private const string AnsiCursorHide = "\u001b[?25l";
	private const string AnsiCursorShow = "\u001b[?25h";
	private const string AnsiClearEol = "\u001b[K";

	/// <summary>
	/// Renders an interactive single-choice menu (radio-style) using arrow keys and mnemonics.
	/// Returns the selected index, or <c>-1</c> if Esc was pressed.
	/// </summary>
	internal int ReadChoiceInteractiveSync(
		string prompt, IReadOnlyList<string> choices, int defaultIndex, CancellationToken ct)
	{
		if (ReplSessionIO.KeyReader is { } keyReader)
		{
			return ReadChoiceInteractiveRemote(choices, defaultIndex, keyReader, ct);
		}

#pragma warning disable MA0045 // Intentionally synchronous — interactive menu rendering runs on calling thread
		ConsoleInputGate.Gate.Wait(ct);
#pragma warning restore MA0045
		try
		{
			return ReadChoiceInteractiveCore(choices, defaultIndex, ct);
		}
		finally
		{
			ConsoleInputGate.Gate.Release();
		}
	}

	private int ReadChoiceInteractiveCore(
		IReadOnlyList<string> choices, int defaultIndex, CancellationToken ct)
	{
		var ctx = PrepareMenuContext(choices);
		var cursor = defaultIndex;
		var menuLines = choices.Count;

		OutLine(string.Empty); // separate from prompt
		Out(AnsiCursorHide);
		RenderChoiceMenu(ctx, cursor);
		WriteHintLine("↑↓ move  Enter select", ctx.Shortcuts, "jump  Esc cancel");

		try
		{
			return RunChoiceKeyLoopSync(ctx, ref cursor, menuLines, ct);
		}
		catch (OperationCanceledException)
		{
			ClearMenuRegion(menuLines); // items above cursor + hint at cursor
			Out(AnsiCursorShow);
			throw;
		}
	}

	private int RunChoiceKeyLoopSync(
		MenuContext ctx, ref int cursor, int menuLines, CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			if (!Console.KeyAvailable)
			{
#pragma warning disable MA0045 // Intentionally synchronous — interactive menu rendering runs on calling thread
				Thread.Sleep(15);
#pragma warning restore MA0045
				continue;
			}

			var key = Console.ReadKey(intercept: true);
			var result = HandleChoiceKey(key, ctx, ref cursor, menuLines);
			if (result is not null)
			{
				return result.Value;
			}
		}

		return -1;
	}

	private int ReadChoiceInteractiveRemote(
		IReadOnlyList<string> choices, int defaultIndex,
		IReplKeyReader keyReader, CancellationToken ct)
	{
		var ctx = PrepareMenuContext(choices);
		var cursor = defaultIndex;
		var menuLines = choices.Count;

		OutLine(string.Empty);
		Out(AnsiCursorHide);
		RenderChoiceMenu(ctx, cursor);
		WriteHintLine("↑↓ move  Enter select", ctx.Shortcuts, "jump  Esc cancel");
		Flush(ct);

		try
		{
			return RunChoiceKeyLoopRemote(ctx, ref cursor, menuLines, keyReader, ct);
		}
		catch (OperationCanceledException)
		{
			ClearMenuRegion(menuLines);
			Out(AnsiCursorShow);
			Flush(CancellationToken.None);
			throw;
		}
	}

	private int RunChoiceKeyLoopRemote(
		MenuContext ctx, ref int cursor, int menuLines,
		IReplKeyReader keyReader, CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
#pragma warning disable VSTHRD002
#pragma warning disable MA0045 // Intentionally synchronous — sync menu loop for remote key reader
			var key = keyReader.ReadKeyAsync(ct).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
#pragma warning restore MA0045
			var result = HandleChoiceKey(key, ctx, ref cursor, menuLines);
			Flush(ct);
			if (result is not null)
			{
				return result.Value;
			}
		}

		return -1;
	}

	private int? HandleChoiceKey(
		ConsoleKeyInfo key, MenuContext ctx, ref int cursor, int menuLines)
	{
		if (key.Key == ConsoleKey.Escape)
		{
			ClearMenuRegion(menuLines);
			Out(AnsiCursorShow);
			return -1;
		}

		if (key.Key is ConsoleKey.Enter or ConsoleKey.Spacebar)
		{
			ClearMenuRegion(menuLines);
			Out(AnsiCursorShow);
			OutLine(ctx.Parsed[cursor].Display);
			return cursor;
		}

		if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
		{
			var previous = cursor;
			cursor = key.Key == ConsoleKey.UpArrow
				? (cursor > 0 ? cursor - 1 : ctx.Parsed.Length - 1)
				: (cursor < ctx.Parsed.Length - 1 ? cursor + 1 : 0);
			UpdateMenuLines(ctx, previous, cursor, isChecked: null);
			return null;
		}

		if (key.KeyChar != '\0' && ctx.ShortcutMap.TryGetValue(char.ToUpperInvariant(key.KeyChar), out var idx))
		{
			ClearMenuRegion(menuLines);
			Out(AnsiCursorShow);
			OutLine(ctx.Parsed[idx].Display);
			return idx;
		}

		return null;
	}

	/// <summary>
	/// Renders an interactive multi-choice menu (checkbox-style) using arrow keys, Space, and mnemonics.
	/// Returns the selected indices array, or <c>null</c> if Esc was pressed.
	/// </summary>
	internal int[]? ReadMultiChoiceInteractiveSync(
		string prompt, IReadOnlyList<string> choices, IReadOnlyList<int> defaults,
		int minSelections, int? maxSelections, CancellationToken ct)
	{
		if (ReplSessionIO.KeyReader is { } keyReader)
		{
			return ReadMultiChoiceInteractiveRemote(
				choices, defaults, minSelections, maxSelections, keyReader, ct);
		}

#pragma warning disable MA0045 // Intentionally synchronous — interactive menu rendering runs on calling thread
		ConsoleInputGate.Gate.Wait(ct);
		try
#pragma warning restore MA0045
		{
			return ReadMultiChoiceInteractiveCore(choices, defaults, minSelections, maxSelections, ct);
		}
		finally
		{
			ConsoleInputGate.Gate.Release();
		}
	}

	private int[]? ReadMultiChoiceInteractiveCore(
		IReadOnlyList<string> choices, IReadOnlyList<int> defaults,
		int minSelections, int? maxSelections, CancellationToken ct)
	{
		var ctx = PrepareMenuContext(choices);
		var selected = InitSelectedArray(choices.Count, defaults);
		var cursor = 0;
		var hasError = false;
		var menuLines = choices.Count;

		OutLine(string.Empty);
		Out(AnsiCursorHide);
		RenderMultiChoiceMenu(ctx, selected, cursor);
		WriteHintLine("↑↓ move  Space toggle", ctx.Shortcuts, "jump  Enter confirm  Esc cancel");

		try
		{
			return RunMultiChoiceKeyLoopSync(
				ctx, selected, ref cursor, ref hasError,
				menuLines, minSelections, maxSelections, ct);
		}
		catch (OperationCanceledException)
		{
			ClearMenuRegion(menuLines, 1 + (hasError ? 1 : 0));
			Out(AnsiCursorShow);
			throw;
		}
	}

	private int[]? RunMultiChoiceKeyLoopSync(
		MenuContext ctx, bool[] selected, ref int cursor, ref bool hasError,
		int menuLines, int minSelections, int? maxSelections, CancellationToken ct)
	{
		var escaped = false;
		while (!ct.IsCancellationRequested)
		{
			if (!Console.KeyAvailable)
			{
#pragma warning disable MA0045 // Intentionally synchronous — interactive menu rendering runs on calling thread
				Thread.Sleep(15);
				continue;
#pragma warning restore MA0045
			}

			var key = Console.ReadKey(intercept: true);
			var result = HandleMultiChoiceKey(
				key, ctx, selected, ref cursor, ref hasError, ref escaped,
				menuLines, minSelections, maxSelections);
			if (escaped)
			{
				return null;
			}

			if (result is not null)
			{
				return result;
			}
		}

		return CollectSelected(selected);
	}

	private int[]? ReadMultiChoiceInteractiveRemote(
		IReadOnlyList<string> choices, IReadOnlyList<int> defaults,
		int minSelections, int? maxSelections, IReplKeyReader keyReader, CancellationToken ct)
	{
		var ctx = PrepareMenuContext(choices);
		var selected = InitSelectedArray(choices.Count, defaults);
		var cursor = 0;
		var hasError = false;
		var menuLines = choices.Count;

		OutLine(string.Empty);
		Out(AnsiCursorHide);
		RenderMultiChoiceMenu(ctx, selected, cursor);
		WriteHintLine("↑↓ move  Space toggle", ctx.Shortcuts, "jump  Enter confirm  Esc cancel");
		Flush(ct);

		try
		{
			return RunMultiChoiceKeyLoopRemote(
				ctx, selected, ref cursor, ref hasError,
				menuLines, minSelections, maxSelections, keyReader, ct);
		}
		catch (OperationCanceledException)
		{
			ClearMenuRegion(menuLines, 1 + (hasError ? 1 : 0));
			Out(AnsiCursorShow);
			Flush(CancellationToken.None);
			throw;
		}
	}

	private int[]? RunMultiChoiceKeyLoopRemote(
		MenuContext ctx, bool[] selected, ref int cursor, ref bool hasError,
		int menuLines, int minSelections, int? maxSelections,
		IReplKeyReader keyReader, CancellationToken ct)
	{
		var escaped = false;
		while (!ct.IsCancellationRequested)
		{
#pragma warning disable VSTHRD002
#pragma warning disable MA0045 // Intentionally synchronous — sync menu loop for remote key reader
			var key = keyReader.ReadKeyAsync(ct).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
#pragma warning restore MA0045
			var result = HandleMultiChoiceKey(
				key, ctx, selected, ref cursor, ref hasError, ref escaped,
				menuLines, minSelections, maxSelections);
			Flush(ct);
			if (escaped)
			{
				return null;
			}

			if (result is not null)
			{
				return result;
			}
		}

		return CollectSelected(selected);
	}

	private int[]? HandleMultiChoiceKey(
		ConsoleKeyInfo key, MenuContext ctx,
		bool[] selected, ref int cursor, ref bool hasError, ref bool escaped,
		int menuLines, int minSelections, int? maxSelections)
	{
		if (key.Key == ConsoleKey.Escape)
		{
			ClearMenuRegion(menuLines, 1 + (hasError ? 1 : 0));
			Out(AnsiCursorShow);
			escaped = true;
			return null;
		}

		if (key.Key == ConsoleKey.Enter)
		{
			return TryConfirmMultiChoice(ctx, selected, hasError, menuLines, minSelections, maxSelections, ref hasError);
		}

		if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
		{
			var previous = cursor;
			cursor = key.Key == ConsoleKey.UpArrow
				? (cursor > 0 ? cursor - 1 : ctx.Parsed.Length - 1)
				: (cursor < ctx.Parsed.Length - 1 ? cursor + 1 : 0);
			UpdateMultiMenuLines(ctx, selected, previous, cursor);
			return null;
		}

		if (key.Key == ConsoleKey.Spacebar)
		{
			selected[cursor] = !selected[cursor];
			UpdateSingleMultiMenuLine(ctx, selected, cursor, isCursor: true);
			return null;
		}

		HandleMultiChoiceShortcut(key, ctx, selected, ref cursor);
		return null;
	}

	private static int[]? TryConfirmMultiChoice(
		MenuContext ctx, bool[] selected, bool hadError,
		int menuLines, int minSelections, int? maxSelections, ref bool hasError)
	{
		var result = CollectSelected(selected);
		if (!IsValidSelection(result, minSelections, maxSelections))
		{
			hasError = true;
			var msg = maxSelections is not null
				? $"Please select between {minSelections} and {maxSelections.Value} option(s)."
				: $"Please select at least {minSelections} option(s).";
			WriteErrorBelow(msg, hadError);
			return null;
		}

		ClearMenuRegion(menuLines, 1 + (hadError ? 1 : 0));
		Out(AnsiCursorShow);
		var selectedLabels = result.Select(i => ctx.Parsed[i].Display).ToArray();
		OutLine(string.Join(", ", selectedLabels));
		return result;
	}

	private void HandleMultiChoiceShortcut(
		ConsoleKeyInfo key, MenuContext ctx, bool[] selected, ref int cursor)
	{
		if (key.KeyChar == '\0' || !ctx.ShortcutMap.TryGetValue(char.ToUpperInvariant(key.KeyChar), out var idx))
		{
			return;
		}

		var previous = cursor;
		cursor = idx;
		selected[idx] = !selected[idx];
		if (previous != cursor)
		{
			UpdateMultiMenuLines(ctx, selected, previous, cursor);
		}
		else
		{
			UpdateSingleMultiMenuLine(ctx, selected, cursor, isCursor: true);
		}
	}

	// ---------- Menu context ----------

	private readonly record struct MenuContext(
		(string Display, char? Shortcut)[] Parsed,
		char?[] Shortcuts,
		Dictionary<char, int> ShortcutMap);

	private static MenuContext PrepareMenuContext(IReadOnlyList<string> choices)
	{
		var shortcuts = MnemonicParser.AssignShortcuts(choices);
		var parsed = new (string Display, char? Shortcut)[choices.Count];
		for (var i = 0; i < choices.Count; i++)
		{
			parsed[i] = MnemonicParser.Parse(choices[i]);
		}

		return new MenuContext(parsed, shortcuts, BuildShortcutMap(shortcuts));
	}

	private static bool[] InitSelectedArray(int count, IReadOnlyList<int> defaults)
	{
		var selected = new bool[count];
		foreach (var idx in defaults)
		{
			selected[idx] = true;
		}

		return selected;
	}

	// ---------- Rendering helpers ----------

	private void RenderChoiceMenu(MenuContext ctx, int cursor)
	{
		for (var i = 0; i < ctx.Parsed.Length; i++)
		{
			Out(RenderMenuLine(ctx.Parsed[i].Display, i == cursor, isChecked: null, ctx.Shortcuts[i]));
			OutLine(string.Empty);
		}
	}

	private void RenderMultiChoiceMenu(MenuContext ctx, bool[] selected, int cursor)
	{
		for (var i = 0; i < ctx.Parsed.Length; i++)
		{
			Out(RenderMenuLine(ctx.Parsed[i].Display, i == cursor, selected[i], ctx.Shortcuts[i]));
			OutLine(string.Empty);
		}
	}

	/// <summary>
	/// Updates two menu lines after a cursor move (single-choice).
	/// Cursor is on the hint line; items are <c>parsed.Length</c> to <c>1</c> lines above.
	/// </summary>
	private void UpdateMenuLines(
		MenuContext ctx, int previousCursor, int newCursor, bool? isChecked)
	{
		var itemCount = ctx.Parsed.Length;

		// Move up from hint line to previous cursor item
		Out($"\u001b[{itemCount - previousCursor}A\r");
		Out(RenderMenuLine(ctx.Parsed[previousCursor].Display, isCursor: false, isChecked, ctx.Shortcuts[previousCursor]));
		Out(AnsiClearEol);

		// Navigate to new cursor item
		WriteCursorVertical(newCursor - previousCursor);

		// Redraw new cursor item (selected)
		Out(RenderMenuLine(ctx.Parsed[newCursor].Display, isCursor: true, isChecked, ctx.Shortcuts[newCursor]));
		Out(AnsiClearEol);

		// Return to hint line
		Out($"\u001b[{itemCount - newCursor}B\r");
	}

	/// <summary>
	/// Updates two menu lines after a cursor move (multi-choice).
	/// </summary>
	private void UpdateMultiMenuLines(
		MenuContext ctx, bool[] selected, int previousCursor, int newCursor)
	{
		var itemCount = ctx.Parsed.Length;

		Out($"\u001b[{itemCount - previousCursor}A\r");
		Out(RenderMenuLine(ctx.Parsed[previousCursor].Display, isCursor: false, selected[previousCursor], ctx.Shortcuts[previousCursor]));
		Out(AnsiClearEol);

		WriteCursorVertical(newCursor - previousCursor);

		Out(RenderMenuLine(ctx.Parsed[newCursor].Display, isCursor: true, selected[newCursor], ctx.Shortcuts[newCursor]));
		Out(AnsiClearEol);

		Out($"\u001b[{itemCount - newCursor}B\r");
	}

	/// <summary>
	/// Redraws a single multi-choice line in place (e.g. after Space toggle).
	/// </summary>
	private void UpdateSingleMultiMenuLine(
		MenuContext ctx, bool[] selected, int index, bool isCursor)
	{
		var linesUp = ctx.Parsed.Length - index;
		Out($"\u001b[{linesUp}A\r");
		Out(RenderMenuLine(ctx.Parsed[index].Display, isCursor, selected[index], ctx.Shortcuts[index]));
		Out(AnsiClearEol);
		Out($"\u001b[{linesUp}B\r");
	}

	private static void WriteCursorVertical(int delta)
	{
		if (delta > 0)
		{
			Out($"\u001b[{delta}B\r");
		}
		else if (delta < 0)
		{
			Out($"\u001b[{-delta}A\r");
		}
		else
		{
			Out("\r");
		}
	}

	private string RenderMenuLine(string label, bool isCursor, bool? isChecked, char? shortcut)
	{
		var selectionStyle = _palette?.SelectionStyle ?? "\u001b[7m";
		var prefix = isChecked switch
		{
			true => isCursor ? "> [x] " : "  [x] ",
			false => isCursor ? "> [ ] " : "  [ ] ",
			null => isCursor ? "  > " : "    ",
		};

		var formattedLabel = MnemonicParser.FormatAnsi(label, shortcut);

		return isCursor
			? string.Concat(selectionStyle, prefix, formattedLabel, AnsiReset)
			: string.Concat(prefix, formattedLabel);
	}

	private static void WriteHintLine(string baseHint, char?[] shortcuts, string suffix)
	{
		var shortcutHints = new List<string>();
		foreach (var sc in shortcuts)
		{
			if (sc is not null)
			{
				shortcutHints.Add(char.ToUpperInvariant(sc.Value).ToString());
			}
		}

		var shortcutDisplay = shortcutHints.Count > 0
			? string.Concat("  ", string.Join('/', shortcutHints), " ", suffix)
			: string.Concat("  ", suffix);
		Out(string.Concat("\u001b[38;5;244m", baseHint, shortcutDisplay, AnsiReset));
	}

	/// <summary>
	/// Clears the menu region. Cursor must be on the hint line.
	/// <paramref name="menuLines"/> = number of item lines above cursor.
	/// <paramref name="extraBelow"/> = lines at/below cursor to clear (1 = hint only, 2 = hint + error).
	/// </summary>
	private static void ClearMenuRegion(int menuLines, int extraBelow = 1)
	{
		var totalLines = menuLines + extraBelow;
		if (menuLines > 0)
		{
			Out($"\u001b[{menuLines}A");
		}

		Out("\r");
		for (var i = 0; i < totalLines; i++)
		{
			Out("\u001b[K\n");
		}

		Out($"\u001b[{totalLines}A\r");
	}

	private static void WriteErrorBelow(string message, bool alreadyHasError)
	{
		if (!alreadyHasError)
		{
			Out(string.Concat("\r\n\u001b[38;5;203m", message, AnsiReset, AnsiClearEol));
			Out("\u001b[1A\u001b[999C");
		}
		else
		{
			Out("\u001b[1B\r");
			Out(string.Concat("\u001b[38;5;203m", message, AnsiReset, AnsiClearEol));
			Out("\u001b[1A\u001b[999C");
		}
	}

	private static bool IsValidSelection(int[] selected, int min, int? max) =>
		selected.Length >= min && (max is null || selected.Length <= max.Value);

	// ---------- I/O routing ----------

#pragma warning disable MA0045 // Intentionally synchronous wrappers — used by sync menu rendering loops
	private static void Out(string text) => ReplSessionIO.Output.Write(text);

	private static void OutLine(string text) => ReplSessionIO.Output.WriteLine(text);
#pragma warning restore MA0045

	private static void Flush(CancellationToken ct)
	{
#pragma warning disable VSTHRD002
#pragma warning disable MA0045 // Intentionally synchronous — sync menu rendering cannot use async flush
		ReplSessionIO.Output.FlushAsync(ct).GetAwaiter().GetResult();
#pragma warning restore MA0045
#pragma warning restore VSTHRD002
	}

	private static Dictionary<char, int> BuildShortcutMap(char?[] shortcuts)
	{
		var map = new Dictionary<char, int>();
		for (var i = 0; i < shortcuts.Length; i++)
		{
			if (shortcuts[i] is { } sc)
			{
				map.TryAdd(char.ToUpperInvariant(sc), i);
			}
		}

		return map;
	}

	private static int[] CollectSelected(bool[] selected)
	{
		var result = new List<int>();
		for (var i = 0; i < selected.Length; i++)
		{
			if (selected[i])
			{
				result.Add(i);
			}
		}

		return [.. result];
	}
}
