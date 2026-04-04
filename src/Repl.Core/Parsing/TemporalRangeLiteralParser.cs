namespace Repl;

internal static class TemporalRangeLiteralParser
{
	public static bool TryParseDateRange(string value, out ReplDateRange range)
	{
		range = default!;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		var span = value.AsSpan();
		if (TrySplitRange(span, out var left, out var right))
		{
			if (!TemporalLiteralParser.TryParseDateOnly(left.ToString(), out var from)
				|| !TemporalLiteralParser.TryParseDateOnly(right.ToString(), out var to))
			{
				return false;
			}

			if (to < from)
			{
				return false;
			}

			range = new ReplDateRange(from, to);
			return true;
		}

		if (TrySplitDuration(span, out left, out var durationPart))
		{
			if (!TemporalLiteralParser.TryParseDateOnly(left.ToString(), out var from)
				|| !TimeSpanLiteralParser.TryParse(durationPart.ToString(), out var duration)
				|| !IsWholeDays(duration))
			{
				return false;
			}

			var to = DateOnly.FromDateTime(from.ToDateTime(TimeOnly.MinValue) + duration);
			if (to < from)
			{
				return false;
			}

			range = new ReplDateRange(from, to);
			return true;
		}

		return false;
	}

	public static bool TryParseDateTimeRange(string value, out ReplDateTimeRange range)
	{
		range = default!;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		var span = value.AsSpan();
		if (TrySplitRange(span, out var left, out var right))
		{
			if (!TemporalLiteralParser.TryParseDateTime(left.ToString(), out var from)
				|| !TemporalLiteralParser.TryParseDateTime(right.ToString(), out var to))
			{
				return false;
			}

			if (to < from)
			{
				return false;
			}

			range = new ReplDateTimeRange(from, to);
			return true;
		}

		if (TrySplitDuration(span, out left, out var durationPart))
		{
			if (!TemporalLiteralParser.TryParseDateTime(left.ToString(), out var from)
				|| !TimeSpanLiteralParser.TryParse(durationPart.ToString(), out var duration))
			{
				return false;
			}

			var to = from + duration;
			if (to < from)
			{
				return false;
			}

			range = new ReplDateTimeRange(from, to);
			return true;
		}

		return false;
	}

	public static bool TryParseDateTimeOffsetRange(string value, out ReplDateTimeOffsetRange range)
	{
		range = default!;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		var span = value.AsSpan();
		if (TrySplitRange(span, out var left, out var right))
		{
			if (!TemporalLiteralParser.TryParseDateTimeOffset(left.ToString(), out var from)
				|| !TemporalLiteralParser.TryParseDateTimeOffset(right.ToString(), out var to))
			{
				return false;
			}

			if (to < from)
			{
				return false;
			}

			range = new ReplDateTimeOffsetRange(from, to);
			return true;
		}

		if (TrySplitDuration(span, out left, out var durationPart))
		{
			if (!TemporalLiteralParser.TryParseDateTimeOffset(left.ToString(), out var from)
				|| !TimeSpanLiteralParser.TryParse(durationPart.ToString(), out var duration))
			{
				return false;
			}

			var to = from + duration;
			if (to < from)
			{
				return false;
			}

			range = new ReplDateTimeOffsetRange(from, to);
			return true;
		}

		return false;
	}

	private static bool TrySplitRange(
		ReadOnlySpan<char> value,
		out ReadOnlySpan<char> left,
		out ReadOnlySpan<char> right)
	{
		left = right = default;
		var index = value.IndexOf("..".AsSpan(), StringComparison.Ordinal);
		if (index <= 0 || index >= value.Length - 2)
		{
			return false;
		}

		left = value[..index];
		right = value[(index + 2)..];
		return left.Length > 0 && right.Length > 0;
	}

	private static bool TrySplitDuration(
		ReadOnlySpan<char> value,
		out ReadOnlySpan<char> left,
		out ReadOnlySpan<char> durationPart)
	{
		left = durationPart = default;
		var index = value.LastIndexOf('@');
		if (index <= 0 || index >= value.Length - 1)
		{
			return false;
		}

		left = value[..index];
		durationPart = value[(index + 1)..];
		return left.Length > 0 && durationPart.Length > 0;
	}

	private static bool IsWholeDays(TimeSpan duration) =>
		duration.Ticks % TimeSpan.TicksPerDay == 0;
}
