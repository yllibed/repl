using System.Globalization;

namespace Repl;

internal static class ParameterValueConverter
{
	public static object? ConvertSingle(string? value, Type targetType, IFormatProvider numericFormatProvider)
	{
		ArgumentNullException.ThrowIfNull(targetType);
		ArgumentNullException.ThrowIfNull(numericFormatProvider);

		var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
		if (value is null)
		{
			return nonNullableType == typeof(bool) ? true : null;
		}

		if (TryConvertWellKnown(value, nonNullableType, numericFormatProvider, out var converted))
		{
			return converted;
		}

		if (nonNullableType.IsEnum)
		{
			return Enum.Parse(nonNullableType, value, ignoreCase: true);
		}

		return Convert.ChangeType(value, nonNullableType, numericFormatProvider);
	}

	private static bool TryConvertWellKnown(
		string value,
		Type nonNullableType,
		IFormatProvider numericFormatProvider,
		out object? converted)
	{
		converted = null;

		if (TryConvertCore(value, nonNullableType, numericFormatProvider, out converted))
		{
			return true;
		}

		if (TryConvertTemporal(value, nonNullableType, out converted))
		{
			return true;
		}

		return false;
	}

	private static bool TryConvertCore(
		string value,
		Type nonNullableType,
		IFormatProvider numericFormatProvider,
		out object? converted)
	{
		converted = nonNullableType switch
		{
			_ when nonNullableType == typeof(string) => value,
			_ when nonNullableType == typeof(bool) => bool.Parse(value),
			_ when nonNullableType == typeof(Guid) => Guid.Parse(value),
			_ when nonNullableType == typeof(Uri) => new Uri(value, UriKind.RelativeOrAbsolute),
			_ when nonNullableType == typeof(double) => double.Parse(
				NormalizeNumericLiteral(value),
				NumberStyles.Float | NumberStyles.AllowThousands,
				numericFormatProvider),
			_ when nonNullableType == typeof(decimal) => decimal.Parse(
				NormalizeNumericLiteral(value),
				NumberStyles.Number,
				numericFormatProvider),
			_ => null,
		};
		if (converted is not null || nonNullableType == typeof(string))
		{
			return true;
		}

		if (nonNullableType == typeof(int))
		{
			converted = IntegerLiteralParser.TryParseInt32(value, out var parsed)
				? parsed
				: throw new FormatException($"'{value}' is not a valid Int32 literal.");
			return true;
		}

		if (nonNullableType == typeof(long))
		{
			converted = IntegerLiteralParser.TryParseInt64(value, out var parsed)
				? parsed
				: throw new FormatException($"'{value}' is not a valid Int64 literal.");
			return true;
		}

		return false;
	}

	private static bool TryConvertTemporal(string value, Type nonNullableType, out object? converted)
	{
		converted = null;
		if (nonNullableType == typeof(DateOnly))
		{
			converted = TemporalLiteralParser.TryParseDateOnly(value, out var parsed)
				? parsed
				: throw new FormatException($"'{value}' is not a valid date literal.");
			return true;
		}

		if (nonNullableType == typeof(DateTime))
		{
			converted = TemporalLiteralParser.TryParseDateTime(value, out var parsed)
				? parsed
				: throw new FormatException($"'{value}' is not a valid date-time literal.");
			return true;
		}

		if (nonNullableType == typeof(TimeOnly))
		{
			converted = TemporalLiteralParser.TryParseTimeOnly(value, out var parsed)
				? parsed
				: throw new FormatException($"'{value}' is not a valid time literal.");
			return true;
		}

		if (nonNullableType == typeof(DateTimeOffset))
		{
			converted = TemporalLiteralParser.TryParseDateTimeOffset(value, out var parsed)
				? parsed
				: throw new FormatException($"'{value}' is not a valid date-time-offset literal.");
			return true;
		}

		if (nonNullableType == typeof(TimeSpan))
		{
			converted = TimeSpanLiteralParser.TryParse(value, out var parsed)
				? parsed
				: throw new FormatException($"'{value}' is not a valid time-span literal.");
			return true;
		}

		return false;
	}

	private static string NormalizeNumericLiteral(string value) =>
		value.IndexOf('_', StringComparison.Ordinal) >= 0
			? value.Replace("_", string.Empty, StringComparison.Ordinal)
			: value;
}
