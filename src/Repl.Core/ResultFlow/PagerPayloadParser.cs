namespace Repl;

using System.Buffers;

internal static class PagerPayloadParser
{
	private static readonly SearchValues<char> PlainTableSeparatorChars = SearchValues.Create("- \t");

	public static ParsedPagerPayload Parse(string payload, PagerHeader? header, bool stripPresentationChrome = true)
	{
		var lines = SplitLines(payload);
		var payloadHeader = DetectHeader(lines);
		var resolvedHeader = header ?? payloadHeader;
		var headerLineCount = payloadHeader.Lines.Count;
		var content = new List<string>();
		for (var i = headerLineCount; i < lines.Count; i++)
		{
			var normalized = NormalizeLine(lines[i]);
			if (resolvedHeader.NormalizedLines.Contains(normalized)
				|| (stripPresentationChrome && IsPageFooterLine(lines[i])))
			{
				continue;
			}

			content.Add(lines[i]);
		}

		return new ParsedPagerPayload(resolvedHeader, content);
	}

	private static PagerHeader DetectHeader(List<string> lines)
	{
		if (lines.Count == 0)
		{
			return PagerHeader.Empty;
		}

		if (lines.Count > 1 && IsPlainTableSeparator(lines[1]))
		{
			return CreateHeader([lines[0], lines[1]]);
		}

		if (IsPlainHumanTableHeader(lines[0]))
		{
			return CreateHeader([lines[0]]);
		}

		return lines[0].Contains("\u001b[1m", StringComparison.Ordinal)
			? CreateHeader([lines[0]])
			: PagerHeader.Empty;
	}

	private static PagerHeader CreateHeader(string[] lines) =>
		new(
			lines,
			lines.Select(NormalizeLine).ToHashSet(StringComparer.Ordinal));

	private static bool IsPlainTableSeparator(string line)
	{
		var text = line.Trim();
		return text.Length > 0
			&& text.AsSpan().IndexOfAnyExcept(PlainTableSeparatorChars) < 0
			&& text.Contains('-', StringComparison.Ordinal);
	}

	private static bool IsPlainHumanTableHeader(string line)
	{
		var text = line.TrimStart();
		return text.StartsWith("# ", StringComparison.Ordinal)
			&& text.Contains("  ", StringComparison.Ordinal);
	}

	private static bool IsPageFooterLine(string line) =>
		line.StartsWith("Showing ", StringComparison.Ordinal)
		&& (line.Contains(" of ", StringComparison.Ordinal)
			|| line.Contains(" result(s).", StringComparison.Ordinal))
		&& (line.EndsWith('.')
			|| line.Contains($"Next data page: rerun with {ReplResultFlowOptionNames.Cursor} ", StringComparison.Ordinal));

	private static List<string> SplitLines(string payload)
	{
		if (string.IsNullOrEmpty(payload))
		{
			return [];
		}

		var lines = new List<string>();
		foreach (var line in payload.AsSpan().EnumerateLines())
		{
			lines.Add(line.ToString());
		}

		if (lines.Count > 0 && lines[^1].Length == 0)
		{
			lines.RemoveAt(lines.Count - 1);
		}

		return lines;
	}

	private static string NormalizeLine(string line)
	{
		if (!line.Contains('\u001b', StringComparison.Ordinal))
		{
			return line.Trim();
		}

		var builder = new System.Text.StringBuilder(line.Length);
		for (var i = 0; i < line.Length; i++)
		{
			if (line[i] == '\u001b')
			{
				if (i + 1 >= line.Length)
				{
					continue;
				}

				if (line[i + 1] != '[')
				{
					continue;
				}

				i += 2;
				while (i < line.Length && (line[i] < '@' || line[i] > '~'))
				{
					i++;
				}

				continue;
			}

			builder.Append(line[i]);
		}

		return builder.ToString().Trim();
	}
}
