namespace Repl;

using System.Buffers;
using Repl.Terminal;

internal static class PagerPayloadParser
{
	private static readonly SearchValues<char> PlainTableSeparatorChars = SearchValues.Create("- \t");

	public static ParsedPagerPayload Parse(string payload, PagerHeader? header, bool stripPresentationChrome = true)
	{
		var lines = SplitLines(payload);
		var payloadHeader = DetectHeader(lines);
		var resolvedHeader = header ?? payloadHeader;
		var content = new List<string>();
		// A continuation's own header is dropped only when it repeats the pinned one. One that names other
		// columns stays in the content, separator included even when it matches the pinned one: JSON rows take
		// their columns from their own keys, so a later page can have others, and its rows would otherwise sit
		// under headings that are not theirs.
		if (header is not null && !RepeatsHeader(header, payloadHeader))
		{
			content.AddRange(payloadHeader.Lines);
		}

		for (var i = payloadHeader.Lines.Count; i < lines.Count; i++)
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

	// Word by word: a repeated header is padded to its own page's column widths, and its separator line with it.
	// A label truncated to a different width on each page does not match, so that header is kept, not lost.
	private static bool RepeatsHeader(PagerHeader pinned, PagerHeader candidate) =>
		pinned.Lines.Count > 0
		&& candidate.Lines.Count > 0
		&& HeaderWords(pinned.Lines[0]).SequenceEqual(HeaderWords(candidate.Lines[0]), StringComparer.Ordinal);

	private static string[] HeaderWords(string line) =>
		NormalizeLine(line).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

	private static PagerHeader CreateHeader(string[] lines) =>
		new(
			lines,
			lines.Select(NormalizeLine).ToHashSet(StringComparer.Ordinal));

	private static bool IsPlainTableSeparator(string line)
	{
		var text = line.AsSpan().Trim();
		return text.Length > 0
			&& text.IndexOfAnyExcept(PlainTableSeparatorChars) < 0
			&& text.Contains('-');
	}

	private static bool IsPlainHumanTableHeader(string line)
	{
		var text = line.AsSpan().TrimStart();
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

		return AnsiTextMetrics.StripControlSequences(line).Trim();
	}
}
