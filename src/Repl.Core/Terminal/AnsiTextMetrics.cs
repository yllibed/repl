namespace Repl.Terminal;

internal static class AnsiTextMetrics
{
	public static int GetVisualLength(string text) =>
		GetVisualLength(text.AsSpan());

	public static int GetVisualLength(ReadOnlySpan<char> text)
	{
		var length = 0;
		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] == '\u001b')
			{
				if (i + 1 >= text.Length)
				{
					continue;
				}

				i = SkipEscapeSequence(text, i);
				continue;
			}

			if (!char.IsControl(text[i]))
			{
				length++;
			}
		}

		return length;
	}

	public static string StripControlSequences(string text)
	{
		if (!text.Contains('\u001b', StringComparison.Ordinal))
		{
			return text;
		}

		return StripControlSequences(text.AsSpan());
	}

	public static string StripControlSequences(ReadOnlySpan<char> text)
	{
		var escapeIndex = text.IndexOf('\u001b');
		if (escapeIndex < 0)
		{
			return text.ToString();
		}

		var builder = new System.Text.StringBuilder(text.Length);
		builder.Append(text[..escapeIndex]);
		for (var i = escapeIndex; i < text.Length; i++)
		{
			if (text[i] == '\u001b')
			{
				if (i + 1 >= text.Length)
				{
					continue;
				}

				i = SkipEscapeSequence(text, i);
				continue;
			}

			builder.Append(text[i]);
		}

		return builder.ToString();
	}

	private static int SkipEscapeSequence(ReadOnlySpan<char> text, int escapeIndex)
	{
		if (escapeIndex + 1 >= text.Length)
		{
			return escapeIndex;
		}

		// ANSI escape sequences are terminal protocol bytes, not columns.
		// CSI covers styling/cursor controls; OSC covers hyperlinks and titles; SS3 covers special keys.
		return text[escapeIndex + 1] switch
		{
			'[' => SkipCsiSequence(text, escapeIndex + 2),
			']' => SkipOscSequence(text, escapeIndex + 2),
			'O' => Math.Min(text.Length - 1, escapeIndex + 2),
			_ => escapeIndex + 1,
		};
	}

	private static int SkipCsiSequence(ReadOnlySpan<char> text, int start)
	{
		var i = start;
		while (i < text.Length && (text[i] < '@' || text[i] > '~'))
		{
			i++;
		}

		return Math.Min(i, text.Length - 1);
	}

	private static int SkipOscSequence(ReadOnlySpan<char> text, int start)
	{
		for (var i = start; i < text.Length; i++)
		{
			if (text[i] == '\u0007')
			{
				return i;
			}

			if (text[i] == '\u001b' && i + 1 < text.Length && text[i + 1] == '\\')
			{
				return i + 1;
			}
		}

		return text.Length - 1;
	}
}
