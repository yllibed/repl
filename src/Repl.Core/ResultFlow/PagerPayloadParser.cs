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
			// Only a continuation repeats a header; within one payload, a line like its first one is data.
			if ((header is not null && resolvedHeader.NormalizedLines.Contains(normalized))
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

		return (lines[0].Contains("\u001b[1m", StringComparison.Ordinal) || StartsBold(lines[0]))
			&& NormalizeLine(lines[0]).Length > 0
			? CreateHeader([lines[0]])
			: PagerHeader.Empty;
	}

	// Bold set by the sequences the line starts with, before any text, within a combined sequence such as the
	// human palette's table header (ESC[1;38;5;221m). A 1 that is an extended colour's argument, as in 38;5;1,
	// is a colour. Bold starting later in the line styles text within it, not a header.
	private static bool StartsBold(string line)
	{
		var start = 0;
		while (line.AsSpan(start).StartsWith("\u001b[", StringComparison.Ordinal))
		{
			var end = start + 2;
			while (end < line.Length && (char.IsAsciiDigit(line[end]) || line[end] == ';'))
			{
				end++;
			}

			if (end >= line.Length || line[end] != 'm')
			{
				return false;
			}

			if (SgrSetsBold(line.AsSpan(start + 2, end - start - 2)))
			{
				return true;
			}

			start = end + 1;
		}

		return false;
	}

	private static bool SgrSetsBold(ReadOnlySpan<char> parameters)
	{
		var colourArguments = 0;
		var colourModeNext = false;
		foreach (var range in parameters.Split(';'))
		{
			var parameter = parameters[range];
			if (colourModeNext)
			{
				// 5 takes a palette index, 2 an RGB triple.
				colourArguments = parameter is "5" ? 1 : parameter is "2" ? 3 : 0;
				colourModeNext = false;
			}
			else if (colourArguments > 0)
			{
				colourArguments--;
			}
			else if (parameter is "38" or "48" or "58")
			{
				colourModeNext = true;
			}
			else if (parameter is "1")
			{
				return true;
			}
		}

		return false;
	}

	// Label by label: a repeated header is padded to its own page's column widths, and its separator line with it.
	// A label truncated to a different width on each page does not match, so that header is kept, not lost.
	private static bool RepeatsHeader(PagerHeader pinned, PagerHeader candidate) =>
		pinned.Lines.Count > 0
		&& candidate.Lines.Count > 0
		&& HeaderLabels(pinned).SequenceEqual(HeaderLabels(candidate), StringComparer.Ordinal);

	// A label can hold spaces, so words alone would take 'first' / 'last name' for 'first last' / 'name', and a
	// narrow table leaves a single space between columns. A plain table's separator spans each column, when the
	// header is aligned on it; a styled header brackets each label in its own escape sequences; failing both,
	// labels are what gaps of two or more spaces leave.
	private static IEnumerable<string> HeaderLabels(PagerHeader header)
	{
		var line = header.Lines[0];
		if (header.Lines.Count > 1
			&& LabelsUnderSeparator(AnsiTextMetrics.StripControlSequences(line), header.Lines[1]) is { } spanned)
		{
			return spanned;
		}

		return line.Contains('\u001b', StringComparison.Ordinal)
			// Each run is split at its own gaps too: a header can also be styled as one run, padding included.
			? AnsiTextMetrics.SplitAtControlSequences(line).SelectMany(static run => SplitAtGaps(run))
			: SplitAtGaps(line);
	}

	// The label over each run of dashes, or null when some of the header lies outside every run: the separator
	// then does not say where its columns are.
	private static List<string>? LabelsUnderSeparator(string line, string separator)
	{
		var labels = new List<string>();
		var checkedUpTo = 0;
		var i = 0;
		while (i < separator.Length)
		{
			if (separator[i] != '-')
			{
				i++;
				continue;
			}

			var start = i;
			while (i < separator.Length && separator[i] == '-')
			{
				i++;
			}

			if (!IsBlank(line, checkedUpTo, start))
			{
				return null;
			}

			labels.Add(start < line.Length ? line[start..Math.Min(i, line.Length)].Trim() : string.Empty);
			checkedUpTo = i;
		}

		return IsBlank(line, checkedUpTo, line.Length) ? labels : null;
	}

	private static string[] SplitAtGaps(string text) =>
		text.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	private static bool IsBlank(string line, int from, int to) =>
		from >= line.Length || line.AsSpan(from, Math.Min(to, line.Length) - from).IsWhiteSpace();

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
