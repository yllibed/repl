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

	/// <summary>
	/// Parses a payload whose transformer declared its layout: its header and footer are exactly the lines the
	/// layout names, so nothing is inferred from styling and no line is dropped for reading like a header or a
	/// footer. The first payload's header is pinned. A later payload's header is dropped when it names the columns
	/// the previous payload showed, and kept when it names others, or its rows would sit under headings that are
	/// not theirs.
	/// </summary>
	/// <param name="payload">The rendered payload.</param>
	/// <param name="header">The pinned header, or <see langword="null"/> for the first payload.</param>
	/// <param name="previousLayout">The previous payload's layout, or <see langword="null"/> for the first payload.</param>
	/// <param name="layout">The layout the payload's transformer declared.</param>
	public static ParsedPagerPayload ParseDeclared(
		string payload,
		PagerHeader? header,
		RenderedLayout? previousLayout,
		RenderedLayout layout)
	{
		var lines = SplitLines(payload);
		var headerLineCount = Math.Min(layout.HeaderLineCount, lines.Count);
		var contentEnd = Math.Max(headerLineCount, lines.Count - layout.FooterLineCount);
		var content = new List<string>(contentEnd);
		if (header is null)
		{
			content.AddRange(lines.Take(headerLineCount..contentEnd));
			return new ParsedPagerPayload(DeclaredHeader(lines, headerLineCount), content);
		}

		var repeatsPreviousColumns = previousLayout is not null && layout.NamesSameColumns(previousLayout);
		content.AddRange(lines.Take((repeatsPreviousColumns ? headerLineCount : 0)..contentEnd));
		return new ParsedPagerPayload(header, content);
	}

	// Only the detected path compares lines with the header, so a declared one needs no normalized forms.
	private static PagerHeader DeclaredHeader(List<string> lines, int headerLineCount) =>
		headerLineCount == 0
			? PagerHeader.Empty
			: new PagerHeader([.. lines.Take(headerLineCount)], PagerHeader.Empty.NormalizedLines);

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
