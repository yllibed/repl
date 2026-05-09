namespace Repl.Terminal;

internal static class AnsiTextMetrics
{
	public static int GetVisualLength(string text)
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

				// ANSI control sequences are terminal protocol bytes, not columns.
				// CSI covers styling/cursor controls; OSC covers hyperlinks and titles; SS3 covers special keys.
				i = text[i + 1] switch
				{
					'[' => SkipCsiSequence(text, i + 2),
					']' => SkipOscSequence(text, i + 2),
					'O' => Math.Min(text.Length - 1, i + 2),
					_ => i + 1,
				};

				continue;
			}

			if (!char.IsControl(text[i]))
			{
				length++;
			}
		}

		return length;
	}

	private static int SkipCsiSequence(string text, int start)
	{
		var i = start;
		while (i < text.Length && (text[i] < '@' || text[i] > '~'))
		{
			i++;
		}

		return Math.Min(i, text.Length - 1);
	}

	private static int SkipOscSequence(string text, int start)
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
