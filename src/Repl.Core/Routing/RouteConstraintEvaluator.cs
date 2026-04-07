namespace Repl;

internal static class RouteConstraintEvaluator
{
	public static bool IsMatch(DynamicRouteSegment segment, string value, ParsingOptions parsingOptions)
	{
		ArgumentNullException.ThrowIfNull(segment);
		ArgumentNullException.ThrowIfNull(parsingOptions);

		return segment.ConstraintKind switch
		{
			RouteConstraintKind.String => true,
			RouteConstraintKind.Alpha => IsAlpha(value),
			RouteConstraintKind.Bool => bool.TryParse(value, out _),
			RouteConstraintKind.Email => IsEmail(value),
			RouteConstraintKind.Uri => IsUri(value),
			RouteConstraintKind.Url => IsUrl(value),
			RouteConstraintKind.Urn => IsUrn(value),
			RouteConstraintKind.Time => TemporalLiteralParser.TryParseTimeOnly(value, out _),
			RouteConstraintKind.Date => TemporalLiteralParser.TryParseDateOnly(value, out _),
			RouteConstraintKind.DateTime => TemporalLiteralParser.TryParseDateTime(value, out _),
			RouteConstraintKind.DateTimeOffset => TemporalLiteralParser.TryParseDateTimeOffset(value, out _),
			RouteConstraintKind.TimeSpan => TimeSpanLiteralParser.TryParse(value, out _),
			RouteConstraintKind.Guid => Guid.TryParse(value, out _),
			RouteConstraintKind.Long => IntegerLiteralParser.TryParseInt64(value, out _),
			RouteConstraintKind.Int => IntegerLiteralParser.TryParseInt32(value, out _),
			RouteConstraintKind.Custom => IsCustom(segment, value, parsingOptions),
			_ => false,
		};
	}

	private static bool IsAlpha(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return false;
		}

		foreach (var character in value)
		{
			if (!char.IsLetter(character))
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsEmail(string value)
	{
		if (!System.Net.Mail.MailAddress.TryCreate(value, out var address))
		{
			return false;
		}

		if (!string.Equals(address.Address, value, StringComparison.Ordinal))
		{
			return false;
		}

		var atIndex = value.LastIndexOf('@');
		if (atIndex < 0 || atIndex == value.Length - 1)
		{
			return false;
		}

		var domain = value[(atIndex + 1)..];
		return domain.Contains('.', StringComparison.Ordinal);
	}

	private static bool IsUri(string value) =>
		Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri;

	private static bool IsUrl(string value)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(uri.Host))
		{
			return false;
		}

		return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsUrn(string value)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsAbsoluteUri)
		{
			return false;
		}

		if (!string.Equals(uri.Scheme, "urn", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var literal = uri.OriginalString;
		return literal.StartsWith("urn:", StringComparison.OrdinalIgnoreCase) && literal.Length > 4;
	}

	private static bool IsCustom(DynamicRouteSegment segment, string value, ParsingOptions parsingOptions)
	{
		if (string.IsNullOrWhiteSpace(segment.CustomConstraintName))
		{
			return false;
		}

		if (!parsingOptions.TryGetRouteConstraint(segment.CustomConstraintName, out var predicate))
		{
			return false;
		}

		return predicate(value);
	}
}
