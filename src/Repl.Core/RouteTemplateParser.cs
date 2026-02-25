namespace Repl;

internal static class RouteTemplateParser
{
	public static RouteTemplate Parse(string template, ParsingOptions parsingOptions)
	{
		template = string.IsNullOrWhiteSpace(template)
			? throw new ArgumentException("Route template cannot be empty.", nameof(template))
			: template.Trim();
		ArgumentNullException.ThrowIfNull(parsingOptions);

		var tokens = template.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var segments = new List<RouteSegment>(tokens.Length);

		foreach (var token in tokens)
		{
			segments.Add(ParseSegment(token, parsingOptions));
		}

		ValidateOptionalSegmentOrder(template, segments);

		return new RouteTemplate(template, segments);
	}

	private static RouteSegment ParseSegment(string token, ParsingOptions parsingOptions)
	{
		if (token.Length < 2
			|| token[0] != '{'
			|| token[^1] != '}')
		{
			return new LiteralRouteSegment(token);
		}

		var body = token[1..^1];
		if (string.IsNullOrWhiteSpace(body))
		{
			throw new InvalidOperationException($"Invalid route segment '{token}'.");
		}

		var parts = body.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length > 2)
		{
			throw new InvalidOperationException(
				$"Invalid constrained route segment '{token}'. Expected format '{{name}}' or '{{name:type}}'.");
		}

		var parameterName = parts[0];
		var isOptional = parameterName.EndsWith('?');
		if (isOptional)
		{
			parameterName = parameterName[..^1];
		}

		if (string.IsNullOrWhiteSpace(parameterName))
		{
			throw new InvalidOperationException($"Invalid parameter name in route segment '{token}'.");
		}

		return parts.Length == 1
			? new DynamicRouteSegment(token, parameterName, RouteConstraintKind.String, isOptional: isOptional)
			: ParseConstraint(token, parameterName, parts[1], parsingOptions, isOptional);
	}

	private static DynamicRouteSegment ParseConstraint(
		string fullSegment,
		string parameterName,
		string token,
		ParsingOptions parsingOptions,
		bool isOptional = false)
	{
		var kind = token.ToLowerInvariant() switch
		{
			"string" => RouteConstraintKind.String,
			"alpha" => RouteConstraintKind.Alpha,
			"bool" => RouteConstraintKind.Bool,
			"email" => RouteConstraintKind.Email,
			"uri" => RouteConstraintKind.Uri,
			"url" => RouteConstraintKind.Url,
			"urn" => RouteConstraintKind.Urn,
			"time" => RouteConstraintKind.Time,
			"date" => RouteConstraintKind.Date,
			"datetime" => RouteConstraintKind.DateTime,
			"date-time" => RouteConstraintKind.DateTime,
			"datetimeoffset" => RouteConstraintKind.DateTimeOffset,
			"date-time-offset" => RouteConstraintKind.DateTimeOffset,
			"timespan" => RouteConstraintKind.TimeSpan,
			"time-span" => RouteConstraintKind.TimeSpan,
			"timeonly" => RouteConstraintKind.Time,
			"time-only" => RouteConstraintKind.Time,
			"dateonly" => RouteConstraintKind.Date,
			"date-only" => RouteConstraintKind.Date,
			"guid" => RouteConstraintKind.Guid,
			"long" => RouteConstraintKind.Long,
			"int" => RouteConstraintKind.Int,
			_ => RouteConstraintKind.Custom,
		};

		if (kind != RouteConstraintKind.Custom)
		{
			return new DynamicRouteSegment(fullSegment, parameterName, kind, isOptional: isOptional);
		}

		if (parsingOptions.TryGetRouteConstraint(token, out _))
		{
			return new DynamicRouteSegment(
				fullSegment,
				parameterName,
				RouteConstraintKind.Custom,
				token,
				isOptional);
		}

		throw new InvalidOperationException(
			$"Unknown route constraint '{token}' in segment '{fullSegment}'.");
	}

	private static void ValidateOptionalSegmentOrder(string template, List<RouteSegment> segments)
	{
		var seenOptional = false;
		foreach (var segment in segments)
		{
			if (segment is DynamicRouteSegment { IsOptional: true })
			{
				seenOptional = true;
			}
			else if (seenOptional)
			{
				throw new InvalidOperationException(
					$"Invalid route template '{template}': required segment cannot follow an optional segment. " +
					"Optional parameters must be trailing.");
			}
		}
	}
}
