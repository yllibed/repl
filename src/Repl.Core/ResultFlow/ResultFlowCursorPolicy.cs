namespace Repl;

internal static class ResultFlowCursorPolicy
{
	public const int MaxLength = 512;

	public static bool TryValidate(string? cursor, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
	{
		if (string.IsNullOrEmpty(cursor))
		{
			error = "The result cursor must be provided with a non-empty value.";
			return false;
		}

		if (cursor.Length > MaxLength)
		{
			error = $"The result cursor cannot exceed {MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters.";
			return false;
		}

		if (cursor[0] == '-')
		{
			error = "The result cursor cannot start like a CLI option.";
			return false;
		}

		foreach (var ch in cursor)
		{
			if (char.IsWhiteSpace(ch))
			{
				error = "The result cursor cannot contain whitespace.";
				return false;
			}

			if (ch < 0x20 || ch == 0x7f || ch is >= '\u0080' and <= '\u009f')
			{
				error = "The result cursor cannot contain control characters.";
				return false;
			}
		}

		error = null;
		return true;
	}

	public static void ValidateOrThrow(string? cursor)
	{
		if (!TryValidate(cursor, out var error))
		{
			throw new InvalidOperationException(error);
		}
	}

	public static string FormatCliContinuation(string? cursor) =>
		TryValidate(cursor, out _)
			? $"{ReplResultFlowOptionNames.Cursor} {cursor}"
			: $"{ReplResultFlowOptionNames.Cursor} <cursor omitted>";
}
