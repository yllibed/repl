using System.Globalization;

namespace Repl;

internal static class TemporalLiteralParser
{
	private static readonly string[] DateFormats = ["yyyy-MM-dd"];
	private static readonly string[] DateTimeFormats =
	[
		"yyyy-MM-dd'T'HH:mm:ss",
		"yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
		"yyyy-MM-dd'T'HH:mm",
		"yyyy-MM-dd HH:mm:ss",
		"yyyy-MM-dd HH:mm:ss.FFFFFFF",
		"yyyy-MM-dd HH:mm",
	];
	private static readonly string[] TimeOnlyFormats =
	[
		"HH:mm",
		"HH:mm:ss",
		"HH:mm:ss.FFFFFFF",
	];
	private static readonly string[] DateTimeOffsetFormats =
	[
		"O",
		"yyyy-MM-dd'T'HH:mmK",
		"yyyy-MM-dd'T'HH:mm:ssK",
		"yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
		"yyyy-MM-dd HH:mmK",
		"yyyy-MM-dd HH:mm:ssK",
		"yyyy-MM-dd HH:mm:ss.FFFFFFFK",
	];

	public static bool TryParseDateOnly(string value, out DateOnly date) =>
		DateOnly.TryParseExact(
			value,
			DateFormats,
			CultureInfo.InvariantCulture,
			DateTimeStyles.None,
			out date);

	public static bool TryParseDateTime(string value, out DateTime dateTime) =>
		DateTime.TryParseExact(
			value,
			DateTimeFormats,
			CultureInfo.InvariantCulture,
			DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
			out dateTime);

	public static bool TryParseTimeOnly(string value, out TimeOnly timeOnly) =>
		TimeOnly.TryParseExact(
			value,
			TimeOnlyFormats,
			CultureInfo.InvariantCulture,
			DateTimeStyles.None,
			out timeOnly);

	public static bool TryParseDateTimeOffset(string value, out DateTimeOffset dateTimeOffset) =>
		DateTimeOffset.TryParseExact(
			value,
			DateTimeOffsetFormats,
			CultureInfo.InvariantCulture,
			DateTimeStyles.AllowWhiteSpaces,
			out dateTimeOffset);
}
