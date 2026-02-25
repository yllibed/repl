namespace Repl;

internal static class RouteResolver
{
	internal readonly record struct RouteResolutionResult(
		RouteMatch? Match,
		RouteConstraintFailure? ConstraintFailure,
		RouteMissingArgumentsFailure? MissingArgumentsFailure);

	internal readonly record struct RouteConstraintFailure(
		RouteDefinition Route,
		DynamicRouteSegment Segment,
		string Value,
		int Score,
		int SegmentIndex);

	internal readonly record struct RouteMissingArgumentsFailure(
		RouteDefinition Route,
		DynamicRouteSegment[] MissingSegments,
		int Score,
		int MatchedSegmentCount);

	public static RouteMatch? Resolve(
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<string> inputTokens,
		ParsingOptions parsingOptions) =>
		ResolveWithDiagnostics(routes, inputTokens, parsingOptions).Match;

	internal static RouteResolutionResult ResolveWithDiagnostics(
		IReadOnlyList<RouteDefinition> routes,
		IReadOnlyList<string> inputTokens,
		ParsingOptions parsingOptions)
	{
		ArgumentNullException.ThrowIfNull(routes);
		ArgumentNullException.ThrowIfNull(inputTokens);
		ArgumentNullException.ThrowIfNull(parsingOptions);

		RouteMatch? bestMatch = null;
		RouteConstraintFailure? bestConstraintFailure = null;
		RouteMissingArgumentsFailure? bestMissingArgumentsFailure = null;

		foreach (var route in routes)
		{
			var candidate = TryMatch(
				route,
				inputTokens,
				parsingOptions,
				out var constraintFailure,
				out var missingArgumentsFailure);
			if (candidate is not null)
			{
				if (bestMatch is null || candidate.Score > bestMatch.Score)
				{
					bestMatch = candidate;
				}
			}
			else if (constraintFailure is not null && IsBetterConstraintFailure(constraintFailure.Value, bestConstraintFailure))
			{
				bestConstraintFailure = constraintFailure;
			}
			else if (missingArgumentsFailure is not null
				&& IsBetterMissingArgumentsFailure(missingArgumentsFailure.Value, bestMissingArgumentsFailure))
			{
				bestMissingArgumentsFailure = missingArgumentsFailure;
			}
		}

		return new RouteResolutionResult(bestMatch, bestConstraintFailure, bestMissingArgumentsFailure);
	}

	private static bool IsBetterConstraintFailure(RouteConstraintFailure candidate, RouteConstraintFailure? currentBest)
	{
		if (currentBest is null)
		{
			return true;
		}

		if (candidate.Score != currentBest.Value.Score)
		{
			return candidate.Score > currentBest.Value.Score;
		}

		if (candidate.SegmentIndex != currentBest.Value.SegmentIndex)
		{
			return candidate.SegmentIndex > currentBest.Value.SegmentIndex;
		}

		return candidate.Route.Template.Segments.Count > currentBest.Value.Route.Template.Segments.Count;
	}

	private static bool IsBetterMissingArgumentsFailure(
		RouteMissingArgumentsFailure candidate,
		RouteMissingArgumentsFailure? currentBest)
	{
		if (currentBest is null)
		{
			return true;
		}

		if (candidate.Score != currentBest.Value.Score)
		{
			return candidate.Score > currentBest.Value.Score;
		}

		if (candidate.MatchedSegmentCount != currentBest.Value.MatchedSegmentCount)
		{
			return candidate.MatchedSegmentCount > currentBest.Value.MatchedSegmentCount;
		}

		return candidate.Route.Template.Segments.Count > currentBest.Value.Route.Template.Segments.Count;
	}

	private static RouteMatch? TryMatch(
		RouteDefinition route,
		IReadOnlyList<string> inputTokens,
		ParsingOptions parsingOptions,
		out RouteConstraintFailure? constraintFailure,
		out RouteMissingArgumentsFailure? missingArgumentsFailure)
	{
		constraintFailure = null;
		missingArgumentsFailure = null;
		var segments = route.Template.Segments;
		var requiredCount = segments.Count(s => s is not DynamicRouteSegment { IsOptional: true });
		if (inputTokens.Count < requiredCount)
		{
			return TryMatchIncomplete(route, inputTokens, parsingOptions, out missingArgumentsFailure);
		}

		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var score = 0;

		for (var i = 0; i < segments.Count; i++)
		{
			if (i >= inputTokens.Count)
			{
				// Remaining segments must all be optional dynamic segments (guaranteed by requiredCount check).
				break;
			}

			var segment = segments[i];
			var token = inputTokens[i];

			if (segment is LiteralRouteSegment literal)
			{
				if (!IsLiteralMatch(route, literal, token, i, segments.Count))
				{
					return null;
				}

				score += 100;
				continue;
			}

			var dynamic = (DynamicRouteSegment)segment;
			if (!RouteConstraintEvaluator.IsMatch(dynamic, token, parsingOptions))
			{
				constraintFailure = new RouteConstraintFailure(
					Route: route,
					Segment: dynamic,
					Value: token,
					Score: score,
					SegmentIndex: i);
				return null;
			}

			values[dynamic.Name] = token;
			score += 10 + dynamic.SpecificityScore;
		}

		var remaining = inputTokens.Skip(segments.Count).ToArray();
		return new RouteMatch(route, values, remaining, score);
	}

	private static RouteMatch? TryMatchIncomplete(
		RouteDefinition route,
		IReadOnlyList<string> inputTokens,
		ParsingOptions parsingOptions,
		out RouteMissingArgumentsFailure? missingArgumentsFailure)
	{
		missingArgumentsFailure = null;
		var segments = route.Template.Segments;
		var score = 0;
		for (var i = 0; i < inputTokens.Count; i++)
		{
			var segment = segments[i];
			var token = inputTokens[i];

			if (segment is LiteralRouteSegment literal)
			{
				if (!IsLiteralMatch(route, literal, token, i, segments.Count))
				{
					return null;
				}

				score += 100;
				continue;
			}

			var dynamic = (DynamicRouteSegment)segment;
			if (!RouteConstraintEvaluator.IsMatch(dynamic, token, parsingOptions))
			{
				return null;
			}

			score += 10 + dynamic.SpecificityScore;
		}

		var missingSegments = segments
			.Skip(inputTokens.Count)
			.OfType<DynamicRouteSegment>()
			.Where(s => !s.IsOptional)
			.ToArray();
		if (missingSegments.Length == 0)
		{
			return null;
		}

		missingArgumentsFailure = new RouteMissingArgumentsFailure(
			Route: route,
			MissingSegments: missingSegments,
			Score: score,
			MatchedSegmentCount: inputTokens.Count);
		return null;
	}

	private static bool IsLiteralMatch(
		RouteDefinition route,
		LiteralRouteSegment segment,
		string token,
		int segmentIndex,
		int segmentCount)
	{
		if (string.Equals(segment.Value, token, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		// Aliases map only to the terminal command segment.
		if (segmentIndex != segmentCount - 1)
		{
			return false;
		}

		return route.Command.Aliases.Any(alias =>
			string.Equals(alias, token, StringComparison.OrdinalIgnoreCase));
	}
}
