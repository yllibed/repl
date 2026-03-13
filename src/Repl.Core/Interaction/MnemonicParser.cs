namespace Repl.Interaction;

/// <summary>
/// Parses mnemonic markers (<c>_X</c>) from choice labels and formats display text.
/// <para>
/// Convention:
/// <list type="bullet">
///   <item><c>"_Abort"</c> → display <c>"Abort"</c>, shortcut <c>'A'</c></item>
///   <item><c>"No_thing"</c> → display <c>"Nothing"</c>, shortcut <c>'t'</c></item>
///   <item><c>"__real"</c> → display <c>"_real"</c>, no shortcut (escaped underscore)</item>
///   <item><c>"Plain"</c> → display <c>"Plain"</c>, no shortcut</item>
/// </list>
/// </para>
/// </summary>
internal static class MnemonicParser
{
	/// <summary>
	/// Parses a label and returns its display text and optional shortcut character.
	/// </summary>
	public static (string Display, char? Shortcut) Parse(string label)
	{
		if (string.IsNullOrEmpty(label))
		{
			return (label ?? string.Empty, null);
		}

		var display = new System.Text.StringBuilder(label.Length);
		char? shortcut = null;
		var i = 0;
		while (i < label.Length)
		{
			if (label[i] == '_')
			{
				if (i + 1 < label.Length && label[i + 1] == '_')
				{
					display.Append('_');
					i += 2;
					continue;
				}

				if (shortcut is null && i + 1 < label.Length)
				{
					shortcut = label[i + 1];
					display.Append(label[i + 1]);
					i += 2;
					continue;
				}

				display.Append('_');
				i++;
				continue;
			}

			display.Append(label[i]);
			i++;
		}

		return (display.ToString(), shortcut);
	}

	/// <summary>
	/// Assigns shortcut characters for a list of labels.
	/// Explicit <c>_X</c> markers are honored first, then auto-assignment fills gaps.
	/// </summary>
	public static char?[] AssignShortcuts(IReadOnlyList<string> labels)
	{
		var results = new char?[labels.Count];
		var parsed = new (string Display, char? Shortcut)[labels.Count];
		var usedChars = new HashSet<char>();

		// Pass 1: parse explicit mnemonics
		for (var i = 0; i < labels.Count; i++)
		{
			parsed[i] = Parse(labels[i]);
			if (parsed[i].Shortcut is { } sc)
			{
				results[i] = sc;
				usedChars.Add(char.ToUpperInvariant(sc));
			}
		}

		// Pass 2: auto-assign for labels without explicit mnemonics
		for (var i = 0; i < labels.Count; i++)
		{
			if (results[i] is not null)
			{
				continue;
			}

			var display = parsed[i].Display;
			var assigned = TryAutoAssignLetter(display, usedChars);
			if (assigned is not null)
			{
				results[i] = assigned;
				usedChars.Add(char.ToUpperInvariant(assigned.Value));
			}
		}

		// Pass 3: assign digits 1-9 for any remaining unassigned
		var nextDigit = 1;
		for (var i = 0; i < labels.Count; i++)
		{
			if (results[i] is not null)
			{
				continue;
			}

			while (nextDigit <= 9 && usedChars.Contains((char)('0' + nextDigit)))
			{
				nextDigit++;
			}

			if (nextDigit <= 9)
			{
				var digit = (char)('0' + nextDigit);
				results[i] = digit;
				usedChars.Add(digit);
				nextDigit++;
			}
		}

		return results;
	}

	/// <summary>
	/// Formats a label for ANSI display: the shortcut character is underlined.
	/// </summary>
	public static string FormatAnsi(string display, char? shortcut, string? underlineStart = null)
	{
		if (shortcut is null || string.IsNullOrEmpty(display))
		{
			return display;
		}

		underlineStart ??= "\u001b[4m";
		const string reset = "\u001b[24m";

		var idx = FindShortcutIndex(display, shortcut.Value);
		if (idx < 0)
		{
			return display;
		}

		return string.Concat(
			display[..idx],
			underlineStart,
			display[idx].ToString(),
			reset,
			display[(idx + 1)..]);
	}

	/// <summary>
	/// Formats a label for plain text display: the shortcut character is wrapped in brackets.
	/// </summary>
	public static string FormatText(string display, char? shortcut)
	{
		if (shortcut is null || string.IsNullOrEmpty(display))
		{
			return display;
		}

		// For digit shortcuts not found in display, prefix with [N]
		if (char.IsDigit(shortcut.Value))
		{
			var digitIdx = FindShortcutIndex(display, shortcut.Value);
			if (digitIdx < 0)
			{
				return string.Concat("[", shortcut.Value.ToString(), "] ", display);
			}
		}

		var idx = FindShortcutIndex(display, shortcut.Value);
		if (idx < 0)
		{
			return string.Concat("[", char.ToUpperInvariant(shortcut.Value).ToString(), "]", display);
		}

		return string.Concat(
			display[..idx],
			"[",
			display[idx].ToString(),
			"]",
			display[(idx + 1)..]);
	}

	private static char? TryAutoAssignLetter(string display, HashSet<char> used)
	{
		if (string.IsNullOrEmpty(display))
		{
			return null;
		}

		// Try first letter
		if (char.IsLetter(display[0]) && !used.Contains(char.ToUpperInvariant(display[0])))
		{
			return display[0];
		}

		// Try remaining letters
		for (var i = 1; i < display.Length; i++)
		{
			if (char.IsLetter(display[i]) && !used.Contains(char.ToUpperInvariant(display[i])))
			{
				return display[i];
			}
		}

		return null;
	}

	private static int FindShortcutIndex(string display, char shortcut)
	{
		for (var i = 0; i < display.Length; i++)
		{
			if (char.ToUpperInvariant(display[i]) == char.ToUpperInvariant(shortcut))
			{
				return i;
			}
		}

		return -1;
	}
}
