using System.Globalization;

namespace Repl;

internal static class IntegerLiteralParser
{
	public static bool TryParseInt32(string value, out int result)
	{
		if (!TryParseInt64(value, out var parsed)
			|| parsed < int.MinValue
			|| parsed > int.MaxValue)
		{
			result = 0;
			return false;
		}

		result = (int)parsed;
		return true;
	}

	public static bool TryParseInt64(string value, out long result)
	{
		if (!TryParseSignedLiteral(value, out var sign, out var literal, out var radix))
		{
			result = 0;
			return false;
		}

		if (!TryParseUnsigned(literal, radix, out var unsignedValue))
		{
			result = 0;
			return false;
		}

		return TryApplySign(sign, unsignedValue, out result);
	}

	private static bool TryParseSignedLiteral(
		string value,
		out int sign,
		out string digits,
		out int radix)
	{
		sign = 1;
		digits = string.Empty;
		radix = 10;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		var trimmed = value.Trim();
		if (trimmed[0] is '+' or '-')
		{
			sign = trimmed[0] == '-' ? -1 : 1;
			trimmed = trimmed[1..];
		}

		if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			radix = 16;
			digits = trimmed[2..];
			return !string.IsNullOrEmpty(digits);
		}

		if (trimmed.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
		{
			radix = 2;
			digits = trimmed[2..];
			return !string.IsNullOrEmpty(digits);
		}

		if (trimmed.EndsWith("b", StringComparison.OrdinalIgnoreCase))
		{
			radix = 2;
			digits = trimmed[..^1];
			return !string.IsNullOrEmpty(digits);
		}

		digits = trimmed;
		return true;
	}

	private static bool TryParseUnsigned(string digits, int radix, out ulong result)
	{
		if (!TryNormalizeDigits(digits, radix, out var normalized))
		{
			result = 0;
			return false;
		}

		if (radix == 10)
		{
			return ulong.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out result);
		}

		result = 0;
		foreach (var ch in normalized)
		{
			if (!TryGetDigitValue(ch, out var digitValue) || digitValue >= radix)
			{
				result = 0;
				return false;
			}

			if (result > (ulong.MaxValue - (ulong)digitValue) / (ulong)radix)
			{
				result = 0;
				return false;
			}

			result = checked(result * (ulong)radix + (ulong)digitValue);
		}

		return true;
	}

	private static bool TryNormalizeDigits(string digits, int radix, out string normalized)
	{
		normalized = digits.Replace("_", string.Empty, StringComparison.Ordinal);
		if (string.IsNullOrEmpty(normalized))
		{
			return false;
		}

		if (radix == 2)
		{
			return normalized.All(ch => ch is '0' or '1');
		}

		if (radix == 16)
		{
			return normalized.All(ch => char.IsAsciiDigit(ch) || (char.ToLowerInvariant(ch) >= 'a' && char.ToLowerInvariant(ch) <= 'f'));
		}

		return normalized.All(char.IsAsciiDigit);
	}

	private static bool TryGetDigitValue(char ch, out int value)
	{
		if (char.IsAsciiDigit(ch))
		{
			value = ch - '0';
			return true;
		}

		var lowered = char.ToLowerInvariant(ch);
		if (lowered >= 'a' && lowered <= 'f')
		{
			value = lowered - 'a' + 10;
			return true;
		}

		value = 0;
		return false;
	}

	private static bool TryApplySign(int sign, ulong unsignedValue, out long result)
	{
		if (sign > 0)
		{
			if (unsignedValue > long.MaxValue)
			{
				result = 0;
				return false;
			}

			result = (long)unsignedValue;
			return true;
		}

		if (unsignedValue == (ulong)long.MaxValue + 1UL)
		{
			result = long.MinValue;
			return true;
		}

		if (unsignedValue > long.MaxValue)
		{
			result = 0;
			return false;
		}

		result = -(long)unsignedValue;
		return true;
	}
}
