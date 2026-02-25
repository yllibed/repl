using System.Globalization;
using System.Xml;

namespace Repl;

internal static class TimeSpanLiteralParser
{
	public static bool TryParse(string value, out TimeSpan timeSpan)
	{
		timeSpan = default;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		var literal = value.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
		if (TimeSpan.TryParseExact(literal, "c", CultureInfo.InvariantCulture, out timeSpan))
		{
			return true;
		}

		if (literal.Contains(':')
			&& TimeSpan.TryParse(literal, CultureInfo.InvariantCulture, out timeSpan))
		{
			return true;
		}

		if (TryParseIso8601(literal, out timeSpan))
		{
			return true;
		}

		return TryParseCompact(literal, out timeSpan);
	}

	private static bool TryParseIso8601(string literal, out TimeSpan timeSpan)
	{
		timeSpan = default;
		if (!literal.StartsWith("P", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		try
		{
			timeSpan = XmlConvert.ToTimeSpan(literal);
			return true;
		}
		catch (FormatException)
		{
			return false;
		}
	}

	private static bool TryParseCompact(string literal, out TimeSpan timeSpan)
	{
		timeSpan = default;
		if (!TryReadSignAndStartIndex(literal, out var sign, out var index))
		{
			return false;
		}

		decimal totalTicks = 0;
		DurationUnit? previousUnit = null;
		var hasToken = false;

		while (true)
		{
			SkipWhitespaces(literal, ref index);
			if (index >= literal.Length)
			{
				break;
			}

			if (!TryReadNumber(literal, ref index, out var number))
			{
				return false;
			}

			DurationUnit unit;
			if (TryReadUnit(literal, ref index, out unit))
			{
				previousUnit = unit;
			}
			else if (!TryResolveImplicitUnit(previousUnit, out unit))
			{
				return false;
			}

			totalTicks += number * GetTicksMultiplier(unit);
			hasToken = true;
		}

		if (!hasToken)
		{
			return false;
		}

		return TryBuildTimeSpan(totalTicks * sign, out timeSpan);
	}

	private static bool TryReadSignAndStartIndex(string literal, out int sign, out int index)
	{
		sign = 1;
		index = 0;
		if (literal.Length == 0)
		{
			return false;
		}

		if (literal[0] == '+' || literal[0] == '-')
		{
			sign = literal[0] == '-' ? -1 : 1;
			index = 1;
		}

		return index < literal.Length;
	}

	private static bool TryReadUnit(string literal, ref int index, out DurationUnit unit)
	{
		unit = default;
		if (index >= literal.Length)
		{
			return false;
		}

		if (index + 1 < literal.Length
			&& (literal[index] == 'm' || literal[index] == 'M')
			&& (literal[index + 1] == 's' || literal[index + 1] == 'S'))
		{
			unit = DurationUnit.Milliseconds;
			index += 2;
			return true;
		}

		var ch = char.ToLowerInvariant(literal[index]);
		switch (ch)
		{
			case 'd':
				unit = DurationUnit.Days;
				break;
			case 'h':
				unit = DurationUnit.Hours;
				break;
			case 'm':
				unit = DurationUnit.Minutes;
				break;
			case 's':
				unit = DurationUnit.Seconds;
				break;
			default:
				return false;
		}

		index++;
		return true;
	}

	private static bool TryReadNumber(string literal, ref int index, out decimal number)
	{
		number = 0;
		var numberStart = index;
		var seenDigit = false;
		var seenDot = false;
		while (index < literal.Length)
		{
			var ch = literal[index];
			if (char.IsDigit(ch))
			{
				seenDigit = true;
				index++;
				continue;
			}

			if (ch == '.' && !seenDot)
			{
				seenDot = true;
				index++;
				continue;
			}

			break;
		}

		if (!seenDigit)
		{
			return false;
		}

		var numberLiteral = literal[numberStart..index];
		return decimal.TryParse(
			numberLiteral,
			NumberStyles.AllowDecimalPoint,
			CultureInfo.InvariantCulture,
			out number);
	}

	private static void SkipWhitespaces(string literal, ref int index)
	{
		while (index < literal.Length && char.IsWhiteSpace(literal[index]))
		{
			index++;
		}
	}

	private static bool TryResolveImplicitUnit(DurationUnit? previousUnit, out DurationUnit unit)
	{
		unit = previousUnit switch
		{
			DurationUnit.Hours => DurationUnit.Minutes,
			DurationUnit.Minutes => DurationUnit.Seconds,
			_ => default,
		};
		return previousUnit is DurationUnit.Hours or DurationUnit.Minutes;
	}

	private static decimal GetTicksMultiplier(DurationUnit unit) =>
		unit switch
		{
			DurationUnit.Days => TimeSpan.TicksPerDay,
			DurationUnit.Hours => TimeSpan.TicksPerHour,
			DurationUnit.Minutes => TimeSpan.TicksPerMinute,
			DurationUnit.Seconds => TimeSpan.TicksPerSecond,
			DurationUnit.Milliseconds => TimeSpan.TicksPerMillisecond,
			_ => throw new InvalidOperationException("Unsupported duration unit."),
		};

	private static bool TryBuildTimeSpan(decimal signedTicks, out TimeSpan timeSpan)
	{
		timeSpan = default;
		if (signedTicks > long.MaxValue || signedTicks < long.MinValue)
		{
			return false;
		}

		var ticks = decimal.ToInt64(decimal.Round(signedTicks, 0, MidpointRounding.AwayFromZero));
		timeSpan = TimeSpan.FromTicks(ticks);
		return true;
	}

	private enum DurationUnit
	{
		None = 0,
		Days = 1,
		Hours = 2,
		Minutes = 3,
		Seconds = 4,
		Milliseconds = 5,
	}
}
