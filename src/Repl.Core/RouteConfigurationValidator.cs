namespace Repl;

internal static class RouteConfigurationValidator
{
	public static void ValidateUnique(RouteTemplate candidate, IEnumerable<RouteTemplate> existingTemplates)
	{
		foreach (var existing in existingTemplates)
		{
			if (IsAmbiguous(candidate, existing))
			{
				throw new InvalidOperationException(
					$"Ambiguous route template '{candidate.Template}' conflicts with '{existing.Template}'.");
			}
		}
	}

	private static bool IsAmbiguous(RouteTemplate left, RouteTemplate right)
	{
		var leftMin = left.Segments.Count(s => s is not DynamicRouteSegment { IsOptional: true });
		var leftMax = left.Segments.Count;
		var rightMin = right.Segments.Count(s => s is not DynamicRouteSegment { IsOptional: true });
		var rightMax = right.Segments.Count;

		// If the arity ranges don't overlap, routes cannot be ambiguous.
		var overlapStart = Math.Max(leftMin, rightMin);
		var overlapEnd = Math.Min(leftMax, rightMax);
		if (overlapStart > overlapEnd)
		{
			return false;
		}

		// Check segment-by-segment for the overlapping prefix length.
		for (var i = 0; i < overlapStart; i++)
		{
			if (!AreSegmentsAmbiguous(left.Segments[i], right.Segments[i]))
			{
				return false;
			}
		}

		return true;
	}

	private static bool AreSegmentsAmbiguous(RouteSegment leftSegment, RouteSegment rightSegment)
	{
		if (leftSegment is LiteralRouteSegment leftLiteral
			&& rightSegment is LiteralRouteSegment rightLiteral)
		{
			return string.Equals(leftLiteral.Value, rightLiteral.Value, StringComparison.OrdinalIgnoreCase);
		}

		if (leftSegment is LiteralRouteSegment || rightSegment is LiteralRouteSegment)
		{
			return false;
		}

		var leftDynamic = (DynamicRouteSegment)leftSegment;
		var rightDynamic = (DynamicRouteSegment)rightSegment;
		if (leftDynamic.ConstraintKind != rightDynamic.ConstraintKind)
		{
			return false;
		}

		if (leftDynamic.ConstraintKind == RouteConstraintKind.Custom
			&& !string.Equals(
				leftDynamic.CustomConstraintName,
				rightDynamic.CustomConstraintName,
				StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return true;
	}
}
