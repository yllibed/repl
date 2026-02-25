using System.Text;

namespace Repl;

internal static class TextTableFormatter
{
	public static string FormatRows(
		IReadOnlyList<string[]> rows,
		int renderWidth,
		bool includeHeaderSeparator,
		TextTableStyle style)
	{
		ArgumentNullException.ThrowIfNull(rows);
		if (rows.Count == 0)
		{
			return string.Empty;
		}

		var columnCount = rows.Max(static row => row.Length);
		if (columnCount == 0)
		{
			return string.Empty;
		}

		var normalizedRows = rows
			.Select(row => row.Length == columnCount
				? row
				: [.. row, .. Enumerable.Repeat(string.Empty, columnCount - row.Length)])
			.ToArray();

		var widths = ComputeColumnWidths(normalizedRows, columnCount);
		var separator = ResolveSeparator(renderWidth, widths.Length);
		var fittedWidths = FitColumnWidths(widths, separator, renderWidth);

		var lines = new List<string>(normalizedRows.Length + (includeHeaderSeparator ? 1 : 0))
		{
			FormatAlignedRow(normalizedRows[0], fittedWidths, separator, style, rowIndex: 0),
		};

		if (includeHeaderSeparator)
		{
			lines.Add(FormatAlignedSeparator(fittedWidths, separator));
		}

		for (var rowIndex = 1; rowIndex < normalizedRows.Length; rowIndex++)
		{
			lines.Add(FormatAlignedRow(normalizedRows[rowIndex], fittedWidths, separator, style, rowIndex));
		}

		return string.Join(Environment.NewLine, lines);
	}

	private static int[] ComputeColumnWidths(IReadOnlyList<string[]> rows, int columnCount)
	{
		var widths = new int[columnCount];
		foreach (var row in rows)
		{
			for (var column = 0; column < row.Length; column++)
			{
				widths[column] = Math.Max(widths[column], row[column].Length);
			}
		}

		return widths;
	}

	private static string FormatAlignedRow(
		string[] cells,
		int[] widths,
		string separator,
		TextTableStyle style,
		int rowIndex)
	{
		var builder = new StringBuilder();
		for (var column = 0; column < cells.Length; column++)
		{
			if (column > 0)
			{
				builder.Append(separator);
			}

			var padRight = column < cells.Length - 1;
			var cell = TruncateAndAlign(cells[column], widths[column], padRight);
			var formatter = style.CellFormatter;
			builder.Append(formatter is null ? cell : formatter(rowIndex, column, cell));
		}

		return builder.ToString();
	}

	private static string FormatAlignedSeparator(int[] widths, string separator)
	{
		var builder = new StringBuilder();
		for (var column = 0; column < widths.Length; column++)
		{
			if (column > 0)
			{
				builder.Append(separator);
			}

			builder.Append(new string('-', widths[column]));
		}

		return builder.ToString();
	}

	private static int[] FitColumnWidths(int[] widths, string separator, int renderWidth)
	{
		const int minWidth = 4;
		var fitted = widths.ToArray();
		var budget = Math.Max(renderWidth, 1);
		if (ComputeLineLength(fitted, separator) <= budget)
		{
			return fitted;
		}

		while (ComputeLineLength(fitted, separator) > budget)
		{
			var widestIndex = FindWidestShrinkableIndex(fitted, minWidth);
			if (widestIndex < 0)
			{
				break;
			}

			fitted[widestIndex]--;
		}

		return fitted;
	}

	private static int FindWidestShrinkableIndex(int[] widths, int minWidth)
	{
		var index = -1;
		var width = minWidth;
		for (var i = 0; i < widths.Length; i++)
		{
			if (widths[i] > width)
			{
				width = widths[i];
				index = i;
			}
		}

		return index;
	}

	private static int ComputeLineLength(int[] widths, string separator) =>
		widths.Sum() + ((widths.Length - 1) * separator.Length);

	private static string ResolveSeparator(int renderWidth, int columnCount)
	{
		if (columnCount <= 1)
		{
			return string.Empty;
		}

		return renderWidth < 100 ? " " : "  ";
	}

	private static string TruncateAndAlign(string value, int width, bool padRight)
	{
		if (value.Length <= width)
		{
			return padRight ? value.PadRight(width) : value;
		}

		if (width <= 3)
		{
			return value[..width];
		}

		var truncated = value[..(width - 3)] + "...";
		return padRight ? truncated.PadRight(width) : truncated;
	}
}
