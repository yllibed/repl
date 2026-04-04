namespace Repl;

internal static class ContextResolver
{
	public static ContextMatch? ResolveExact(
		IReadOnlyList<ContextDefinition> contexts,
		IReadOnlyList<string> tokens,
		ParsingOptions parsingOptions)
	{
		ArgumentNullException.ThrowIfNull(contexts);
		ArgumentNullException.ThrowIfNull(tokens);
		ArgumentNullException.ThrowIfNull(parsingOptions);

		foreach (var context in contexts.OrderByDescending(item => item.Template.Segments.Count))
		{
			if (context.Template.Segments.Count != tokens.Count)
			{
				continue;
			}

			if (TryMatch(context, tokens, parsingOptions, out var values))
			{
				return new ContextMatch(context, values);
			}
		}

		return null;
	}

	public static IReadOnlyList<ContextMatch> ResolvePrefixes(
		IReadOnlyList<ContextDefinition> contexts,
		IReadOnlyList<string> tokens,
		ParsingOptions parsingOptions)
	{
		ArgumentNullException.ThrowIfNull(contexts);
		ArgumentNullException.ThrowIfNull(tokens);
		ArgumentNullException.ThrowIfNull(parsingOptions);

		var matches = new List<ContextMatch>();
		foreach (var context in contexts)
		{
			if (context.Template.Segments.Count > tokens.Count)
			{
				continue;
			}

			if (TryMatch(context, tokens, parsingOptions, out var values))
			{
				matches.Add(new ContextMatch(context, values));
			}
		}

		return matches
			.OrderBy(match => match.Context.Template.Segments.Count)
			.ToArray();
	}

	private static bool TryMatch(
		ContextDefinition context,
		IReadOnlyList<string> tokens,
		ParsingOptions parsingOptions,
		out IReadOnlyDictionary<string, string> values)
	{
		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var segments = context.Template.Segments;
		for (var i = 0; i < segments.Count; i++)
		{
			var token = tokens[i];
			var segment = segments[i];
			if (segment is LiteralRouteSegment literal)
			{
				if (!string.Equals(literal.Value, token, StringComparison.OrdinalIgnoreCase))
				{
					values = map;
					return false;
				}

				continue;
			}

			var dynamic = (DynamicRouteSegment)segment;
			if (!RouteConstraintEvaluator.IsMatch(dynamic, token, parsingOptions))
			{
				values = map;
				return false;
			}

			map[dynamic.Name] = token;
		}

		values = map;
		return true;
	}
}
