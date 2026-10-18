using System.Globalization;

namespace Repl;

internal static class ParameterValueConverter
{
	internal static object? ConvertNative(object? value, Type targetType, IFormatProvider provider, bool enumIgnoreCase = true)
	{
		if (value is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Null }) { value = null; }
		// Reconstructing a compatible array as a collection would lose its rank and lower bounds.
		if (value is Array && targetType.IsInstanceOfType(value)) { return value; }
		if (value is not null && HandlerArgumentBinder.TryGetCollectionElementType(targetType, out var elementType))
		{
			var items = value is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Array } array
				? array.EnumerateArray().Select(static item => (object?)item.Clone())
				: value is System.Collections.IEnumerable collection && value is not string ? collection.Cast<object?>() : [value];
			return HandlerArgumentBinder.ConvertMatchesToTargetCollection(targetType, elementType,
				items.Select(item => ConvertNative(item, elementType, provider, enumIgnoreCase)).ToArray());
		}
		if (value is System.Text.Json.JsonElement json)
		{
			if (targetType == typeof(string))
			{
				return json.ValueKind == System.Text.Json.JsonValueKind.String ? json.GetString() : json.GetRawText();
			}
			if (json.ValueKind == System.Text.Json.JsonValueKind.Number)
			{
				return ConvertSingle(json.GetRawText(), targetType, CultureInfo.InvariantCulture, enumIgnoreCase);
			}
			value = json.ValueKind switch
			{
				System.Text.Json.JsonValueKind.String => json.GetString(),
				System.Text.Json.JsonValueKind.True => true,
				System.Text.Json.JsonValueKind.False => false,
				System.Text.Json.JsonValueKind.Null => null,
				_ => throw new InvalidCastException("Invocation values must be scalars or collections of scalars."),
			};
		}
		if (value is null)
		{
			if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
			{
				throw new InvalidCastException($"Null cannot be bound to {targetType.Name}.");
			}
			return null;
		}
		if (targetType.IsInstanceOfType(value)) { return value; }
		if (value is string text) { return ConvertSingle(text, targetType, provider, enumIgnoreCase); }
		var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
		if (type == typeof(bool) || value is bool)
		{
			return ConvertSingle(Convert.ToString(value, CultureInfo.InvariantCulture), type, CultureInfo.InvariantCulture, enumIgnoreCase);
		}
		if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
			|| type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
		{
			if ((value is decimal number && decimal.Truncate(number) != number)
				|| (value is double real && Math.Truncate(real) != real)
				|| (value is float single && MathF.Truncate(single) != single))
			{
				throw new InvalidCastException($"A fractional value cannot be bound to {type.Name}.");
			}
		}
		if (type.IsEnum) { return Enum.ToObject(type, value); }
		return Convert.ChangeType(value, type, provider);
	}

	public static object? ConvertSingle(
		string? value,
		Type targetType,
		IFormatProvider numericFormatProvider,
		bool enumIgnoreCase = true)
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
			return Enum.Parse(nonNullableType, value, ignoreCase: enumIgnoreCase);
		}

		return Convert.ChangeType(value, nonNullableType, numericFormatProvider);
	}

	// Non-throwing convertibility check for completion: filters provider values that could
	// never bind to the target option/argument (e.g. "abc" for an int option). Mirrors
	// ConvertSingle's rules so completion and binding cannot disagree.
	//
	// unwrapCollections distinguishes the two binding shapes: for an OPTION or a repeated
	// positional a single candidate is one ELEMENT of the collection (checked against the
	// element type), but a dynamic ROUTE SEGMENT binds its single value through
	// ConvertSingle(routeValue, parameterType) against the WHOLE type — which throws for a
	// collection target — so element unwrapping there would wrongly accept a value that can
	// never bind. Route-segment callers pass false.
	public static bool CanConvert(
		string value,
		Type parameterType,
		IFormatProvider numericFormatProvider,
		bool enumIgnoreCase = true,
		bool unwrapCollections = true)
	{
		ArgumentNullException.ThrowIfNull(parameterType);
		ArgumentNullException.ThrowIfNull(numericFormatProvider);

		var targetType = unwrapCollections
			&& HandlerArgumentBinder.TryGetCollectionElementType(parameterType, out var elementType)
			? elementType
			: parameterType;
		try
		{
			_ = ConvertSingle(value, targetType, numericFormatProvider, enumIgnoreCase);
			return true;
		}
		catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or ArgumentException)
		{
			return false;
		}
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

		if (TryConvertTemporalRange(value, nonNullableType, out converted))
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
			_ when nonNullableType == typeof(FileInfo) => new FileInfo(value),
			_ when nonNullableType == typeof(DirectoryInfo) => new DirectoryInfo(value),
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

	private static bool TryConvertTemporalRange(string value, Type nonNullableType, out object? converted)
	{
		converted = null;
		if (nonNullableType == typeof(ReplDateRange))
		{
			converted = TemporalRangeLiteralParser.TryParseDateRange(value, out var parsed)
				? parsed
				: throw new FormatException($"'{value}' is not a valid date range literal. Use start..end or start@duration with whole days.");
			return true;
		}

		if (nonNullableType == typeof(ReplDateTimeRange))
		{
			converted = TemporalRangeLiteralParser.TryParseDateTimeRange(value, out var parsed)
				? parsed
				: throw new FormatException($"'{value}' is not a valid date-time range literal. Use start..end or start@duration.");
			return true;
		}

		if (nonNullableType == typeof(ReplDateTimeOffsetRange))
		{
			converted = TemporalRangeLiteralParser.TryParseDateTimeOffsetRange(value, out var parsed)
				? parsed
				: throw new FormatException($"'{value}' is not a valid date-time-offset range literal. Use start..end or start@duration.");
			return true;
		}

		return false;
	}

	private static string NormalizeNumericLiteral(string value) =>
		value.IndexOf('_', StringComparison.Ordinal) >= 0
			? value.Replace("_", string.Empty, StringComparison.Ordinal)
			: value;
}
